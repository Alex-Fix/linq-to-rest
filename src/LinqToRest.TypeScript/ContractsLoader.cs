using System.Reflection;
using System.Runtime.Loader;
using LinqToRest.Modeling;

namespace LinqToRest.TypeScript;

/// <summary>Loads a contracts assembly and builds its DTO model, as the server does.</summary>
internal static class ContractsLoader
{
    public static DtoModel Load(string assemblyPath)
    {
        var path = Path.GetFullPath(assemblyPath);
        if (!File.Exists(path))
        {
            throw new GeneratorException($"The contracts assembly '{path}' doesn't exist. Build the contracts project first.");
        }

        var context = new ContractsLoadContext(path);
        var assembly = context.LoadFromAssemblyPath(path);
        var referenced = assembly.GetReferencedAssemblies().FirstOrDefault(name => name.Name == AbstractionsName);
        var own = typeof(DtoModel).Assembly.GetName();
        if (referenced is null)
        {
            throw new GeneratorException($"'{assembly.GetName().Name}' doesn't reference LinqToRest.Abstractions, so it has no DTO model.");
        }

        if (referenced.Version?.Major != own.Version?.Major)
        {
            throw new GeneratorException($"'{assembly.GetName().Name}' uses LinqToRest.Abstractions {referenced.Version}, but this tool is built for {own.Version}. Use the matching linqtorest-ts version.");
        }

        return new DtoModelBuilder().ApplyConfigurationsFromAssembly(assembly).Build();
    }

    private const string AbstractionsName = "LinqToRest.Abstractions";

    /// <summary>
    /// Resolves the contracts' dependencies from their .deps.json, except LinqToRest.Abstractions, which must be the
    /// tool's own copy so the configuration interfaces are the same types.
    /// </summary>
    private sealed class ContractsLoadContext(string path) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == AbstractionsName)
            {
                return typeof(DtoModel).Assembly;
            }

            return _resolver.ResolveAssemblyToPath(assemblyName) is { } resolved ? LoadFromAssemblyPath(resolved) : null;
        }
    }
}

internal sealed class GeneratorException(string message) : Exception(message);

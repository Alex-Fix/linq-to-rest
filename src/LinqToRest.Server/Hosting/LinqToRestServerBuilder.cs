using System.Reflection;
using LinqToRest.Protocol;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.Hosting;

/// <summary>Configures the LinqToRest server: mappings, DbContext, limits and policies.</summary>
public sealed class LinqToRestServerBuilder
{
    internal LinqToRestServerBuilder(IServiceCollection services) => Services = services;

    /// <summary>Gets the service collection.</summary>
    public IServiceCollection Services { get; }

    internal List<MappingRegistration> Mappings { get; } = [];

    internal Type? ContextType { get; private set; }

    internal QueryLimits QueryLimits { get; } = new();

    /// <summary>Adds a DTO mapping.</summary>
    /// <param name="mapping">The mapping.</param>
    /// <typeparam name="TDto">The DTO type.</typeparam>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddMapping<TDto, TEntity>(IDtoMapping<TDto, TEntity> mapping)
        where TDto : class, IDto
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(mapping);
        Mappings.Add(new MappingRegistration<TDto, TEntity>(mapping));
        return this;
    }

    /// <summary>Adds every non-abstract <see cref="IDtoMapping{TDto, TEntity}"/> with a parameterless constructor in an assembly.</summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddMappingsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var add = typeof(LinqToRestServerBuilder).GetMethod(nameof(AddMapping))!;
        var mappings = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false } && type.GetConstructor(Type.EmptyTypes) is not null)
            .SelectMany(type => type.GetInterfaces()
                .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IDtoMapping<,>))
                .Select(contract => (Type: type, Arguments: contract.GetGenericArguments())))
            .OrderBy(mapping => mapping.Type.FullName, StringComparer.Ordinal);
        foreach (var (type, arguments) in mappings)
        {
            add.MakeGenericMethod(arguments).Invoke(this, [Activator.CreateInstance(type)]);
        }

        return this;
    }

    /// <summary>Sets the DbContext queries and batches run against (resolved per request from DI).</summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder UseDbContext<TContext>()
        where TContext : DbContext
    {
        ContextType = typeof(TContext);
        return this;
    }

    /// <summary>Changes the request limits (body size, depth, nodes, batch size…).</summary>
    /// <param name="configure">Changes the limits.</param>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder Limits(Action<QueryLimits> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(QueryLimits);
        return this;
    }

    /// <summary>Registers a <see cref="DtoSetPolicy{TDto, TEntity}"/> as a scoped service.</summary>
    /// <typeparam name="TPolicy">The policy type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddPolicy<TPolicy>()
        where TPolicy : class
    {
        var policyBase = typeof(TPolicy);
        while (policyBase is not null && !(policyBase.IsGenericType && policyBase.GetGenericTypeDefinition() == typeof(DtoSetPolicy<,>)))
        {
            policyBase = policyBase.BaseType;
        }

        Services.AddScoped(policyBase ?? throw new ArgumentException($"{typeof(TPolicy).Name} doesn't derive from DtoSetPolicy<TDto, TEntity>.", nameof(TPolicy)), typeof(TPolicy));
        return this;
    }
}

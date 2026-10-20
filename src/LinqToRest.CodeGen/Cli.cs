using LinqToRest.Modeling;

namespace LinqToRest.CodeGen;

/// <summary>The command line of linqtorest-gen.</summary>
internal static class Cli
{
    private const string Usage = """
        Usage: linqtorest-gen ts --model <model.json> --name <Name> --out <file.ts> [--import <module>] [--check]
               linqtorest-gen cs --model <model.json> --namespace <Namespace> --context <Context> --out <file.cs> [--check]

          ts           Generate a TypeScript client.
          cs           Generate a C# client: classes, the context and its Add<Context> registration.
          --model      The model document a LinqToRest server exported (ExportLinqToRestModelAsync).
          --name       ts: the prefix of the generated context: Shop -> createShop(), ShopContext, shopModel.
          --import     ts: the module the generated file imports from (default: linqtorest).
          --namespace  cs: the namespace of the generated types.
          --context    cs: the context class name, for example ShopContext.
          --out        The file to write.
          --check      Write nothing; exit 1 when the file differs (for CI).
        """;

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter errors)
    {
        if (args.Count == 0 || args[0] is not ("ts" or "cs"))
        {
            errors.WriteLine(Usage);
            return 2;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var check = false;
        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--check":
                    check = true;
                    break;
                case "--model" or "--name" or "--out" or "--import" or "--namespace" or "--context" when index + 1 < args.Count:
                    options[args[index]] = args[++index];
                    break;
                default:
                    errors.WriteLine(Usage);
                    return 2;
            }
        }

        var typeScript = args[0] == "ts";
        string[] required = typeScript ? ["--model", "--name", "--out"] : ["--model", "--namespace", "--context", "--out"];
        string[] allowed = [.. required, .. typeScript ? ["--import"] : Array.Empty<string>()];
        if (!required.All(options.ContainsKey) || !options.Keys.All(allowed.Contains))
        {
            errors.WriteLine(Usage);
            return 2;
        }

        var modelPath = options["--model"];
        var path = options["--out"];

        try
        {
            var model = Load(modelPath);
            var source = Path.GetFileName(modelPath);
            string text;
            if (typeScript)
            {
                var emitter = new TypeScriptEmitter(model, options["--name"], options.GetValueOrDefault("--import", "linqtorest"), source);
                text = emitter.Emit();
                foreach (var warning in emitter.Warnings)
                {
                    errors.WriteLine($"warning: {warning}");
                }
            }
            else
            {
                text = new CSharpEmitter(model, options["--namespace"], options["--context"], source).Emit();
            }

            if (check)
            {
                var current = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
                if (current == text)
                {
                    output.WriteLine($"{path} is up to date.");
                    return 0;
                }

                errors.WriteLine($"{path} is out of date. Run linqtorest-gen without --check to regenerate it.");
                return 1;
            }

            File.WriteAllText(path, text);
            output.WriteLine($"Wrote {path}.");
            return 0;
        }
        catch (Exception exception) when (exception is GeneratorException or FormatException or IOException)
        {
            errors.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static RestModel Load(string path)
    {
        var full = Path.GetFullPath(path);
        return File.Exists(full)
            ? RestModel.Parse(File.ReadAllBytes(full))
            : throw new GeneratorException($"The model document '{full}' doesn't exist. Export it from the server first.");
    }
}

internal sealed class GeneratorException(string message) : Exception(message);

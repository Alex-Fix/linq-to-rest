namespace RestToLinq.TypeScript;

/// <summary>The command line of resttolinq-ts.</summary>
internal static class Cli
{
    private const string Usage = """
        Usage: resttolinq-ts --assembly <Contracts.dll> --name <Name> --out <file.ts> [--import <module>] [--check]

          --assembly  The built contracts assembly with the DTOs and their IDtoTypeConfiguration classes.
          --name      The prefix of the generated context: Shop -> createShop(), ShopContext, shopModel.
          --out       The TypeScript file to write.
          --import    The module the generated file imports from (default: resttolinq).
          --check     Write nothing; exit 1 when the file differs (for CI).
        """;

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter errors)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var check = false;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--check":
                    check = true;
                    break;
                case "--assembly" or "--name" or "--out" or "--import" when index + 1 < args.Count:
                    options[args[index]] = args[++index];
                    break;
                default:
                    errors.WriteLine(Usage);
                    return 2;
            }
        }

        if (!options.TryGetValue("--assembly", out var assembly) || !options.TryGetValue("--name", out var name) || !options.TryGetValue("--out", out var path))
        {
            errors.WriteLine(Usage);
            return 2;
        }

        try
        {
            var model = ContractsLoader.Load(assembly);
            var emitter = new TypeScriptEmitter(model, name, options.GetValueOrDefault("--import", "resttolinq"), Path.GetFileNameWithoutExtension(assembly));
            var text = emitter.Emit();
            foreach (var warning in emitter.Warnings)
            {
                errors.WriteLine($"warning: {warning}");
            }

            if (check)
            {
                var current = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null;
                if (current == text)
                {
                    output.WriteLine($"{path} is up to date.");
                    return 0;
                }

                errors.WriteLine($"{path} is out of date. Run resttolinq-ts without --check to regenerate it.");
                return 1;
            }

            File.WriteAllText(path, text);
            output.WriteLine($"Wrote {path}.");
            return 0;
        }
        catch (Exception exception) when (exception is GeneratorException or InvalidOperationException or IOException)
        {
            errors.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }
}

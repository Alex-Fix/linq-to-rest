using LinqToRest.Modeling;

namespace LinqToRest.CodeGen.UnitTests;

public sealed class GeneratorTests
{
    // Copied next to the tests: CI builds map source paths to /_/, so [CallerFilePath] can't find the repository.
    private static readonly string GoldenModel = Path.Combine(AppContext.BaseDirectory, "golden", "model.json");

    [Fact]
    public async Task TypeScript_output_for_the_golden_model_is_the_typescript_test_model()
    {
        var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "typescript", "shop.generated.ts"), TestContext.Current.CancellationToken);
        var model = RestModel.Parse(await File.ReadAllBytesAsync(GoldenModel, TestContext.Current.CancellationToken));

        var actual = new TypeScriptEmitter(model, "Shop", "../src/index.js", "model.json").Emit();

        Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void Check_passes_for_an_up_to_date_file_and_fails_for_a_stale_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ts");
        try
        {
            var (written, _, _) = Run("ts", "--model", GoldenModel, "--name", "Shop", "--out", path);
            var (current, output, _) = Run("ts", "--model", GoldenModel, "--name", "Shop", "--out", path, "--check");
            File.AppendAllText(path, "// edited\n");
            var (stale, _, errors) = Run("ts", "--model", GoldenModel, "--name", "Shop", "--out", path, "--check");

            Assert.Equal((0, 0, 1), (written, current, stale));
            Assert.Contains("up to date", output, StringComparison.Ordinal);
            Assert.Contains("out of date", errors, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Warns_about_number_precision()
    {
        var (code, _, errors) = Run("ts", "--model", GoldenModel, "--name", "Shop", "--out", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ts"));

        Assert.Equal(0, code);
        Assert.Contains("warning: ProductDto.price is decimal", errors, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], 2, "Usage")]
    [InlineData(new[] { "--model", "model.json", "--name", "Shop", "--out", "x.ts" }, 2, "Usage")]
    [InlineData(new[] { "ts", "--model", "missing.json", "--name", "Shop", "--out", "x.ts" }, 1, "doesn't exist")]
    [InlineData(new[] { "ts", "--model", "a.json", "--bogus" }, 2, "Usage")]
    [InlineData(new[] { "ts", "--name", "Shop", "--out", "x.ts" }, 2, "Usage")]
    public void Rejects_bad_arguments(string[] args, int expected, string message)
    {
        var (code, _, errors) = Run(args);

        Assert.Equal(expected, code);
        Assert.Contains(message, errors, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_invalid_model_document()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"v":2,"sets":[]}""");
        try
        {
            var (code, _, errors) = Run("ts", "--model", path, "--name", "Shop", "--out", Path.Combine(Path.GetTempPath(), "unused.ts"));

            Assert.Equal(1, code);
            Assert.Contains("error: The model document version 2 isn't supported", errors, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Maps_every_scalar_kind()
    {
        var emitter = new TypeScriptEmitter(KindsModel(), "Kinds", "linqtorest", "test.json");

        var text = emitter.Emit();

        Assert.Contains("readonly id: number;", text, StringComparison.Ordinal);
        Assert.Contains("readonly flag: boolean | null;", text, StringComparison.Ordinal);
        Assert.Contains("readonly flag: Expr<boolean | null>;", text, StringComparison.Ordinal);
        Assert.Contains("readonly at: DateTimeExpr<string | null>;", text, StringComparison.Ordinal);
        Assert.Contains("readonly duration: OrderedExpr<string>;", text, StringComparison.Ordinal);
        Assert.Contains("readonly blob: Expr<string | null>;", text, StringComparison.Ordinal);
        Assert.Contains("readonly time: TimeOnlyExpr;", text, StringComparison.Ordinal);
        Assert.Contains("readonly small: NumberExpr;", text, StringComparison.Ordinal);
        Assert.Contains("readonly numbers: PrimitiveCollectionExpr<NumberExpr>;", text, StringComparison.Ordinal);
        Assert.Contains("export type Access = \"Read\" | \"Write\";", text, StringComparison.Ordinal);
        Assert.Contains("readonly access: OrderedExpr<Access>;", text, StringComparison.Ordinal);
        Assert.Contains("kind: \"datetimeoffset\"", text, StringComparison.Ordinal);
        Assert.Contains("kind: \"binary\"", text, StringComparison.Ordinal);
        Assert.Contains("long: JavaScript numbers are exact only up to 2^53.", text, StringComparison.Ordinal);
        Assert.Contains(emitter.Warnings, warning => warning.StartsWith("Kinds.big is long", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_only_sets_get_no_write_operations()
    {
        var text = new TypeScriptEmitter(KindsModel(), "Kinds", "linqtorest", "test.json").Emit();

        Assert.Contains("readonly kinds: RestSet<Kinds, KindsProxy>;", text, StringComparison.Ordinal);
        Assert.Contains("writes: [],", text, StringComparison.Ordinal);
    }

    private static (int Code, string Output, string Errors) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = Cli.Run(args, output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    private static RestModel KindsModel() => RestModel.Parse("""
        {
          "v": 1,
          "enums": {"Access": ["Read", "Write"]},
          "sets": [{
            "name": "kinds", "type": "Kinds", "key": ["id"],
            "pagination": {"modes": ["offset"], "defaultPageSize": 10, "maxPageSize": 10, "maxSkip": 0},
            "properties": [
              {"name": "id", "type": "int32", "sortable": true},
              {"name": "flag", "type": "bool", "nullable": true},
              {"name": "at", "type": "dateTimeOffset", "nullable": true},
              {"name": "duration", "type": "timeSpan"},
              {"name": "blob", "type": "bytes", "nullable": true},
              {"name": "time", "type": "timeOnly"},
              {"name": "small", "type": "uint8"},
              {"name": "numbers", "type": "int32", "collection": true},
              {"name": "access", "type": "Access"},
              {"name": "big", "type": "int64"}
            ]
          }]
        }
        """);
}

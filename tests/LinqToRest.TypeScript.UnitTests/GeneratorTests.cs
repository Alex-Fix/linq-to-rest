using LinqToRest.Modeling;
using LinqToRest.TestContracts;

namespace LinqToRest.TypeScript.UnitTests;

public sealed class GeneratorTests
{
    private static readonly string Contracts = typeof(ProductDto).Assembly.Location;

    [Fact]
    public async Task Output_for_the_test_contracts_is_the_typescript_test_model()
    {
        var expected = await File.ReadAllTextAsync(TypeScriptTestModel(), TestContext.Current.CancellationToken);

        var actual = new TypeScriptEmitter(ContractsLoader.Load(Contracts), "Shop", "../src/index.js", "LinqToRest.TestContracts").Emit();

        Assert.Equal(expected.ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void Check_passes_for_an_up_to_date_file_and_fails_for_a_stale_one()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ts");
        try
        {
            var (written, _, _) = Run("--assembly", Contracts, "--name", "Shop", "--out", path);
            var (current, output, _) = Run("--assembly", Contracts, "--name", "Shop", "--out", path, "--check");
            File.AppendAllText(path, "// edited\n");
            var (stale, _, errors) = Run("--assembly", Contracts, "--name", "Shop", "--out", path, "--check");

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
        var (code, _, errors) = Run("--assembly", Contracts, "--name", "Shop", "--out", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ts"));

        Assert.Equal(0, code);
        Assert.Contains("warning: ProductDto.Price is decimal", errors, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0], 2, "Usage")]
    [InlineData(new[] { "--assembly", "missing.dll", "--name", "Shop", "--out", "x.ts" }, 1, "doesn't exist")]
    [InlineData(new[] { "--assembly", "a.dll", "--bogus" }, 2, "Usage")]
    public void Rejects_bad_arguments(string[] args, int expected, string message)
    {
        var (code, _, errors) = Run(args);

        Assert.Equal(expected, code);
        Assert.Contains(message, errors, StringComparison.Ordinal);
    }

    [Fact]
    public void Maps_every_scalar_kind()
    {
        var model = new DtoModelBuilder().Dto<KindsDto>(b => b.ToSet("kinds").HasKey(d => d.Id)).Build();
        var emitter = new TypeScriptEmitter(model, "Kinds", "linqtorest", "Test");

        var text = emitter.Emit();

        Assert.Contains("readonly id: number;", text, StringComparison.Ordinal);
        Assert.Contains("flag: boolean | null;", text, StringComparison.Ordinal);
        Assert.Contains("readonly flag: Expr<boolean | null>;", text, StringComparison.Ordinal);
        Assert.Contains("readonly at: DateTimeExpr<string | null>;", text, StringComparison.Ordinal);
        Assert.Contains("readonly duration: OrderedExpr<string>;", text, StringComparison.Ordinal);
        Assert.Contains("readonly blob: Expr<string | null>;", text, StringComparison.Ordinal);
        Assert.Contains("readonly time: TimeOnlyExpr;", text, StringComparison.Ordinal);
        Assert.Contains("readonly numbers: PrimitiveCollectionExpr<NumberExpr>;", text, StringComparison.Ordinal);
        Assert.Contains("export type FileAccess = string;", text, StringComparison.Ordinal);
        Assert.Contains("kind: \"datetimeoffset\"", text, StringComparison.Ordinal);
        Assert.Contains("long: JavaScript numbers are exact only up to 2^53.", text, StringComparison.Ordinal);
        Assert.Contains(emitter.Warnings, warning => warning.StartsWith("KindsDto.Big is long", StringComparison.Ordinal));
    }

    [Fact]
    public void Rejects_dtos_with_the_same_name()
    {
        var model = new DtoModelBuilder()
            .Dto<First.SameDto>(b => b.ToSet("first").HasKey(d => d.Id))
            .Dto<Second.SameDto>(b => b.ToSet("second").HasKey(d => d.Id))
            .Build();

        var error = Assert.Throws<GeneratorException>(() => new TypeScriptEmitter(model, "X", "linqtorest", "Test").Emit());

        Assert.Contains("Two DTOs are named SameDto", error.Message, StringComparison.Ordinal);
    }

    private static (int Code, string Output, string Errors) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var code = Cli.Run(args, output, errors);
        return (code, output.ToString(), errors.ToString());
    }

    // Copied next to the tests: CI builds map source paths to /_/, so [CallerFilePath] can't find the repository.
    private static string TypeScriptTestModel() => Path.Combine(AppContext.BaseDirectory, "typescript", "shop.generated.ts");

    public sealed class KindsDto : IDto
    {
        public int Id { get; set; }
        public bool? Flag { get; set; }
        public DateTimeOffset? At { get; set; }
        public TimeSpan Duration { get; set; }
        public byte[]? Blob { get; set; }
        public TimeOnly Time { get; set; }
        public List<int> Numbers { get; set; } = [];
        public FileAccess Access { get; set; }
        public long Big { get; set; }
    }

    public static class First
    {
        public sealed class SameDto : IDto
        {
            public int Id { get; set; }
        }
    }

    public static class Second
    {
        public sealed class SameDto : IDto
        {
            public int Id { get; set; }
        }
    }
}

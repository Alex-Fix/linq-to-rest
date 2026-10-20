using BenchmarkDotNet.Attributes;
using LinqToRest.CodeGen;
using LinqToRest.Modeling;

namespace LinqToRest.Benchmarks;

/// <summary>linqtorest-gen: reading the model document and emitting TypeScript.</summary>
public class Generator
{
    private byte[] _document = [];
    private RestModel _model = null!;

    [Params("golden", "shop")]
    public string Model { get; set; } = "golden";

    [GlobalSetup]
    public void Setup()
    {
        _document = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, Model == "shop" ? "Shop" : "golden", "model.json"));
        _model = ReadModel();
    }

    [Benchmark]
    public RestModel ReadModel() => RestModel.Parse(_document);

    [Benchmark]
    public string EmitTypeScript() => new TypeScriptEmitter(_model, "Shop", "linqtorest", "model.json").Emit();
}

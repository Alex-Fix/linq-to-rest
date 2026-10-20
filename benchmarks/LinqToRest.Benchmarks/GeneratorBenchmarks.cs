using System.Reflection;
using BenchmarkDotNet.Attributes;
using LinqToRest.Modeling;
using LinqToRest.TypeScript;

namespace LinqToRest.Benchmarks;

/// <summary>linqtorest-ts: building the DTO model and emitting TypeScript.</summary>
public class Generator
{
    private Assembly _assembly = null!;
    private DtoModel _model = null!;

    [Params("test-contracts", "shop")]
    public string Contracts { get; set; } = "test-contracts";

    [GlobalSetup]
    public void Setup()
    {
        _assembly = Contracts == "shop" ? typeof(Server.IntegrationTests.Shop.ShopModel).Assembly : typeof(TestContracts.ProductDto).Assembly;
        _model = BuildModel();
    }

    [Benchmark]
    public DtoModel BuildModel() => new DtoModelBuilder().ApplyConfigurationsFromAssembly(_assembly).Build();

    [Benchmark]
    public string Emit() => new TypeScriptEmitter(_model, "Shop", "linqtorest", "Contracts").Emit();
}

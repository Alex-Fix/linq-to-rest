using System.Globalization;
using System.Net;
using System.Text;
using BenchmarkDotNet.Attributes;
using LinqToRest.Client;
using LinqToRest.Client.Translation;
using LinqToRest.Protocol;
using LinqToRest.TestContracts;

namespace LinqToRest.Benchmarks;

public sealed class BenchmarkShop(RestDataContextOptions<BenchmarkShop> options) : RestDataContext(options)
{
    public DtoSet<ProductDto> Products => Set<ProductDto>();

    /// <summary>A context whose HTTP calls answer with <paramref name="response"/>, without a network.</summary>
    public static BenchmarkShop Create(byte[] response) =>
        new(new RestDataContextOptions<BenchmarkShop>(SharedModel, new HttpClient(new CannedHandler(response)) { BaseAddress = new Uri("https://shop.test/api/") }));

    private static readonly Modeling.DtoModel SharedModel = TestModel.Create();

    private sealed class CannedHandler(byte[] response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(response);
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

/// <summary>LINQ → query tree → JSON body, including captured-variable evaluation.</summary>
public class ClientTranslation
{
    private IQueryable _query = null!;

    [Params("small", "medium", "large")]
    public string Shape { get; set; } = "small";

    [GlobalSetup]
    public void Setup()
    {
        var shop = BenchmarkShop.Create("""{"items":[]}"""u8.ToArray());
        var minimum = 10m;
        var ids = Enumerable.Range(1, 300).ToList();
        _query = Shape switch
        {
            "small" => shop.Products.Where(p => p.Price > minimum).Take(20),
            "medium" => shop.Products
                .Where(p => p.Tags.Any(t => t == "sale") && p.Lines.Sum(l => l.Quantity * l.UnitPrice) > minimum && p.Category!.Name != "Tools")
                .OrderByDescending(p => p.Price)
                .Select(p => new
                {
                    p.Id,
                    Label = p.Name + " (" + p.CategoryName + ")",
                    Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
                    Skus = p.Lines.Where(l => l.Quantity > 1).Select(l => l.Sku).ToList(),
                })
                .Take(20),
            _ => shop.Products
                .Where(p => ids.Contains(p.Id) && (p.Name.Contains('o') || p.CreatedAt.Year == 2026) && p.Lines.Any(l => l.Quantity > 0))
                .Include(p => p.Category)
                .Include(p => p.Lines)
                .OrderBy(p => p.Price)
                .Take(20),
        };
    }

    [Benchmark]
    public byte[] Translate()
    {
        var query = TranslatedQuery.Translate(_query.Expression);
        return QueryJson.Write(query.ToRequest(RestQueryProvider.Page(query, query.Take ?? 50)));
    }
}

/// <summary>Response JSON → tracked DTOs.</summary>
public class ClientMaterialization
{
    private byte[] _response = [];

    [Params(1, 50, 200)]
    public int Rows { get; set; } = 1;

    [GlobalSetup]
    public void Setup()
    {
        var items = Enumerable.Range(1, Rows).Select(id =>
            $$$"""{"id":{{{id}}},"name":"Product {{{id}}}","description":null,"price":{{{(id % 90 + 0.5).ToString(CultureInfo.InvariantCulture)}}},"status":"Active","version":"{{{Guid.NewGuid()}}}","createdAt":"2026-01-10T09:30:00Z","releasedOn":"2026-02-01","categoryId":2,"tags":["sale","new"],"categoryName":"Games"}""");
        _response = Encoding.UTF8.GetBytes($$"""{"items":[{{string.Join(",", items)}}]}""");
    }

    [Benchmark]
    public async Task<int> Materialize()
    {
        var shop = BenchmarkShop.Create(_response);
        return (await shop.Products.Take(Rows).ToListAsync()).Count;
    }
}

/// <summary>Change detection over tracked DTOs and building the $batch (10% modified).</summary>
public class ChangeDetection
{
    private BenchmarkShop _shop = null!;

    [Params(50, 500)]
    public int Tracked { get; set; } = 50;

    [GlobalSetup]
    public void Setup()
    {
        // An empty result keeps the entries modified, so every iteration builds the same batch.
        _shop = BenchmarkShop.Create("""{"results":[]}"""u8.ToArray());
        for (var id = 1; id <= Tracked; id++)
        {
            var product = new ProductDto { Id = id, Name = $"Product {id}", Price = id, Version = Guid.NewGuid(), Tags = ["sale"] };
            _shop.Products.Attach(product);
            if (id % 10 == 0)
            {
                product.Price += 1;
            }
        }
    }

    [Benchmark]
    public Task<int> SaveChanges() => _shop.SaveChangesAsync();
}

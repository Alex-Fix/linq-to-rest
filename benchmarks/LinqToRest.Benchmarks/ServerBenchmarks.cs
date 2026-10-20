using System.Buffers;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using LinqToRest.Protocol;
using LinqToRest.Server.Batch;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.IntegrationTests.Shop;
using LinqToRest.Server.Query;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinqToRest.Benchmarks;

/// <summary>Bytes → query tree, with every limit enforced.</summary>
public class QueryReading
{
    private readonly QueryLimits _limits = new();
    private byte[] _body = [];

    [Params("small", "medium", "large")]
    public string Shape { get; set; } = "small";

    [GlobalSetup]
    public void Setup() => _body = Encoding.UTF8.GetBytes(Queries.Get(Shape));

    [Benchmark]
    public QueryRequest Read() => QueryJson.Read(_body, _limits);
}

/// <summary>Query tree → plan: type checking and expression building.</summary>
public class QueryBinding
{
    private ServerFixture _fixture = null!;
    private QueryRequest _request = null!;

    [Params("small", "medium", "large")]
    public string Shape { get; set; } = "small";

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new ServerFixture(products: 0);
        _request = ServerFixture.Parse(Queries.Get(Shape));
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark]
    public object Bind() => QueryCompiler.Compile(_fixture.Server, _fixture.Products, _request, _fixture.Policies());
}

/// <summary>Plan → rows on SQLite → response JSON, against the same query written by hand in EF Core LINQ.</summary>
public class QueryExecution
{
    private ServerFixture _fixture = null!;
    private QueryRequest _request = null!;
    private readonly ArrayBufferWriter<byte> _buffer = new(64 * 1024);

    [Params("small", "medium")]
    public string Shape { get; set; } = "small";

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new ServerFixture();
        _request = ServerFixture.Parse(Queries.Get(Shape));
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark(Baseline = true)]
    public async Task<int> HandWrittenEfCore()
    {
        await using var db = _fixture.NewDbContext();
        _buffer.ResetWrittenCount();
        await using var writer = new Utf8JsonWriter(_buffer);
        if (Shape == "small")
        {
            var rows = await db.Products.Where(p => p.Price > 10).OrderBy(p => p.Id).Take(20)
                .Select(p => new
                {
                    p.Id,
                    p.Name,
                    p.Description,
                    p.Price,
                    p.Status,
                    p.Version,
                    p.CreatedAt,
                    p.ReleasedOn,
                    p.Rating,
                    p.IsFeatured,
                    p.CategoryId,
                    Tags = p.Tags.OrderBy(t => t.Value).Select(t => t.Value).ToList(),
                    CategoryName = p.Category != null ? p.Category.Name : null,
                })
                .ToListAsync();
            JsonSerializer.Serialize(writer, new { items = rows }, Json);
        }
        else
        {
            var filtered = db.Products.Where(p => p.Tags.Any(t => t.Value == "sale") && p.Lines.Sum(l => l.Quantity * l.UnitPrice) > 10 && p.Category!.Name != "Tools");
            var total = await filtered.LongCountAsync();
            var rows = await filtered.OrderByDescending(p => p.Price).ThenBy(p => p.Id).Take(20)
                .Select(p => new
                {
                    p.Id,
                    Label = p.Name + " (" + (p.Category != null ? p.Category.Name : null) + ")",
                    Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
                    Skus = p.Lines.Where(l => l.Quantity > 1).OrderBy(l => l.Id).Select(l => l.Sku).ToList(),
                })
                .ToListAsync();
            JsonSerializer.Serialize(writer, new { items = rows, totalCount = total }, Json);
        }

        return _buffer.WrittenCount;
    }

    [Benchmark]
    public async Task<int> LinqToRest()
    {
        await using var db = _fixture.NewDbContext();
        var plan = QueryCompiler.Compile(_fixture.Server, _fixture.Products, _request, _fixture.Policies());
        var result = await _fixture.Products.ExecuteAsync(db, plan, CancellationToken.None);
        _buffer.ResetWrittenCount();
        await using var writer = new Utf8JsonWriter(_buffer);
        QueryEndpoint.Write(writer, plan.Shape, result);
        await writer.FlushAsync();
        return _buffer.WrittenCount;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>A whole QUERY through ASP.NET Core's test server: read, bind, execute, write.</summary>
public class QueryEndpointRequest
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private SqliteConnection _connection = null!;
    private byte[] _body = [];

    [Params("small", "medium")]
    public string Shape { get; set; } = "small";

    [GlobalSetup]
    public async Task Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<ShopDbContext>(options => options.UseSqlite(_connection));
        builder.Services.AddLinqToRest(ShopModel.Create(), options => options.UseDbContext<ShopDbContext>().AddMappingsFromAssembly(typeof(ServerFixture).Assembly));
        _app = builder.Build();
        _app.MapLinqToRest("/api");
        await _app.StartAsync();
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            await db.Database.EnsureCreatedAsync();
            ShopData.Seed(db);
        }

        _client = _app.GetTestClient();
        _body = Encoding.UTF8.GetBytes(Queries.Get(Shape));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Benchmark]
    public async Task<int> Query()
    {
        using var content = new ByteArrayContent(_body);
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Query, "/api/products");
        request.Content = content;
        using var response = await _client.SendAsync(request);
        return (await response.Content.ReadAsByteArrayAsync()).Length;
    }
}

/// <summary>$batch: reading the body, and applying 1 or 20 operations in a transaction.</summary>
public class BatchExecution
{
    private ServerFixture _fixture = null!;
    private byte[] _body = [];
    private BatchRequest _batch = null!;
    private readonly QueryLimits _limits = new();

    [Params(1, 20)]
    public int Operations { get; set; } = 1;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new ServerFixture(products: 0);
        var operations = Enumerable.Range(0, Operations).Select(index => index % 2 == 0
            ? $$$"""{"id":"o{{{index}}}","op":"insert","set":"categories","values":{"name":"New {{{index}}}"}}"""
            : $$$"""{"id":"o{{{index}}}","op":"update","set":"categories","key":{"id":{{{1 + (index % 3)}}}},"values":{"name":"Renamed {{{index}}}"}}""");
        _body = Encoding.UTF8.GetBytes($$$"""{"v":1,"operations":[{{{string.Join(",", operations)}}}]}""");
        _batch = BatchJson.Read(_body, _limits);
    }

    [GlobalCleanup]
    public void Cleanup() => _fixture.Dispose();

    [Benchmark]
    public BatchRequest Read() => BatchJson.Read(_body, _limits);

    [Benchmark]
    public async Task<int> Execute()
    {
        await using var db = _fixture.NewDbContext();
        var response = await new BatchExecutor(_fixture.Server, db, _fixture.Policies()).ExecuteAsync(_batch, CancellationToken.None);
        return response.Results.Count;
    }
}

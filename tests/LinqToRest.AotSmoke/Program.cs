using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.AotSmoke;
using LinqToRest.Client;

// Runs the client's main paths against canned responses and exits non-zero on the first mismatch. Under Native AOT
// with reflection-based JSON off, anything still relying on reflection or dynamic code fails here.

var scenarios = new (string Name, Func<Task> Run)[]
{
    ("filter, sort and full rows", FilterSortAndFullRows),
    ("includes and cursor pages", IncludesAndCursorPages),
    ("projections", Projections),
    ("count and any", CountAndAny),
    ("tracking and save", TrackingAndSave),
};

var failed = 0;
foreach (var (name, run) in scenarios)
{
    try
    {
        await run();
        Console.WriteLine($"ok    {name}");
    }
#pragma warning disable CA1031 // Any failure is reported, and the exit code fails the CI step.
    catch (Exception exception)
#pragma warning restore CA1031
    {
        failed++;
        Console.WriteLine($"FAIL  {name}: {exception}");
    }
}

return failed == 0 ? 0 : 1;

static async Task FilterSortAndFullRows()
{
    var (shop, http) = Create();
    http.Respond($$"""{"items":[{{Rows.Chess}}]}""");
    var minimum = 10m;
    var since = new DateTime(2026, 1, 1);
    var version = new Guid("00000001-0000-0000-0000-000000000000");

    // Comparisons and arithmetic on decimal, dates and Guid compile to operator lookups that trimming could remove.
    var rows = await shop.Products
        .Where(p => p.Price > minimum && p.Price * 2 - 1 <= 100 && p.Status == ProductStatus.Active && p.Name.StartsWith("Ch"))
        .Where(p => p.CreatedAt >= since && p.ReleasedOn != null && p.ReleasedOn < new DateOnly(2027, 1, 1) && p.Version == version)
        .OrderByDescending(p => p.Price)
        .ThenBy(p => p.Id)
        .Take(10)
        .ToListAsync();

    var chess = Single(rows);
    Check(chess is { Id: 1, Name: "Chess", Price: 20m, Status: ProductStatus.Active, CategoryId: 2, CategoryName: "Games" }, "row values");
    Check(chess.ReleasedOn == new DateOnly(2026, 2, 1) && chess.CreatedAt == new DateTime(2026, 1, 10, 9, 30, 0), "dates");
    Check(chess.Tags.SequenceEqual(["sale", "new"]), "primitive collection");
    Check(http.LastBody.Contains("\"Active\"", StringComparison.Ordinal) && http.LastBody.Contains("10", StringComparison.Ordinal), "request body");
}

static async Task IncludesAndCursorPages()
{
    var (shop, http) = Create();
    var withNavigations = Rows.Chess[..^1] + $$""","category":{{Rows.Games}},"lines":{{Rows.Lines}}}""";
    http.Respond($$"""{"items":[{{withNavigations}}],"nextCursor":"abc"}""")
        .Respond("""{"items":[],"totalCount":1}""");

    var page = await shop.Products.Include(p => p.Category).Include(p => p.Lines).OrderBy(p => p.Id).Take(1).ToCursorPageAsync();
    var next = await shop.Products.OrderBy(p => p.Id).After(page.NextCursor!).Take(1).ToCursorPageAsync(includeTotalCount: true);

    var chess = Single(page.Items);
    Check(chess.Category is { Id: 2, Name: "Games" }, "reference navigation");
    Check(chess.Lines.Select(l => (l.Sku, l.Quantity, l.UnitPrice)).SequenceEqual([("C1", 2, 20m), ("C2", 1, 5.5m)]), "collection navigation");
    Check(page.NextCursor == "abc" && next.Items.Count == 0 && next.NextCursor is null && next.TotalCount == 1, "cursor pages");
    Check(http.LastBody.Contains("abc", StringComparison.Ordinal), "cursor sent");
}

// The C# compiler builds anonymous-type and object-initializer projections with Expression.New(…, members) and
// Expression.Bind(MethodInfo, …), which System.Linq.Expressions marks as trim-unsafe; this scenario proves they work.
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Projection expression trees; verified by running this app.")]
static async Task Projections()
{
    var (shop, http) = Create();
    http.Respond($$"""{"items":[{"id":1,"label":"Chess!","total":45.5,"lines":[{"sku":"C1","quantity":2}],"skus":["C1","C2"],"quantities":[2,1],"tags":["sale"],"category":{{Rows.Games}},"price":20}]}""")
        .Respond("""{"items":[{"name":"Chess","status":"Active"}]}""")
        .Respond("""{"items":[{"name":"Chess","quantities":[2,1]}]}""")
        .Respond("""{"items":["Chess","Go"]}""")
        .Respond("""{"items":[20,null]}""");

    var anonymous = Single(await shop.Products
        .Select(p => new
        {
            p.Id,
            Label = p.Name + "!",
            Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
            Lines = p.Lines.Select(l => new { l.Sku, l.Quantity }).ToList(),
            Skus = p.Lines.Select(l => l.Sku).ToArray(),
            Quantities = p.Lines.Select(l => l.Quantity).ToList(),
            p.Tags,
            p.Category,
            Price = (double)p.Price,
        })
        .Take(1)
        .ToListAsync());
    var record = Single(await shop.Products.Select(p => new NameAndStatus(p.Name, p.Status)).Take(1).ToListAsync());
    var initialized = Single(await shop.Products.Select(p => new NameAndQuantities { Name = p.Name, Quantities = p.Lines.Select(l => l.Quantity).ToList() }).Take(1).ToListAsync());
    var names = await shop.Products.Select(p => p.Name).Take(2).ToListAsync();
    var prices = await shop.Products.Select(p => (decimal?)p.Price).Take(2).ToListAsync();

    Check((anonymous.Id, anonymous.Label, anonymous.Total, anonymous.Price) == (1, "Chess!", 45.5m, 20d), "anonymous scalars");
    Check(anonymous.Lines is [{ Sku: "C1", Quantity: 2 }], "list of anonymous objects");
    Check(anonymous.Skus.SequenceEqual(["C1", "C2"]) && anonymous.Quantities.SequenceEqual([2, 1]) && anonymous.Tags.SequenceEqual(["sale"]), "lists of values");
    Check(anonymous.Category is { Id: 2, Name: "Games" }, "navigation in a projection");
    Check(record == new NameAndStatus("Chess", ProductStatus.Active), "record constructor");
    Check(initialized is { Name: "Chess", Quantities: [2, 1] }, "object initializer");
    Check(names.SequenceEqual(["Chess", "Go"]) && prices.SequenceEqual([20m, null]), "scalar selects");
}

static async Task CountAndAny()
{
    var (shop, http) = Create();
    http.Respond("""{"items":[],"totalCount":3}""").Respond("""{"items":[{}]}""");

    var count = await shop.Products.CountAsync(p => p.Tags.Contains("sale"));
    var any = await shop.Products.AnyAsync(p => p.Lines.Any(l => l.Quantity > 1));

    Check(count == 3 && any, "count and any");
}

static async Task TrackingAndSave()
{
    var (shop, http) = Create();
    http.Respond($$"""{"items":[{{Rows.Chess}}]}""");
    var chess = Single(await shop.Products.Take(1).ToListAsync());
    chess.Price = 21;
    var cube = new ProductDto { Name = "Cube", Price = 9.5m, Category = new CategoryDto { Name = "Puzzles" } };
    shop.Products.Add(cube);
    shop.Categories.Add(cube.Category);
    http.Respond($$$"""
        {"results":[
          {"id":"o0","value":{"id":7,"name":"Puzzles"}},
          {"id":"o1","value":{"id":42,"name":"Cube","price":9.5,"status":"Draft","version":"00000009-0000-0000-0000-000000000000","createdAt":"2026-10-18T12:00:00","categoryId":7,"tags":[],"categoryName":"Puzzles"}},
          {"id":"o2","value":{{{Rows.Chess.Replace("\"price\":20", "\"price\":21", StringComparison.Ordinal).Replace("00000001-", "00000002-", StringComparison.Ordinal)}}}}]}
        """);

    Check(shop.Entry(chess).State == EntityState.Modified && shop.Entry(chess).Property(p => p.Price).IsModified, "change detection");
    Check(await shop.SaveChangesAsync() == 3, "saved operations");

    using var batch = JsonDocument.Parse(http.LastBody);
    var operations = batch.RootElement.GetProperty("operations").EnumerateArray().Select(op => (op.GetProperty("op").GetString(), op.GetProperty("set").GetString())).ToList();
    Check(operations.Count == 3 && operations.SequenceEqual([("insert", "categories"), ("insert", "products"), ("update", "products")]), "batch operations");
    Check(chess.Version == new Guid("00000002-0000-0000-0000-000000000000") && (cube.Id, cube.CategoryId, cube.Category.Id) == (42, 7, 7), "saved values written back");
    Check(shop.Entry(cube).State == EntityState.Unchanged, "saved state");
}

static (ShopContext Shop, StubHandler Http) Create()
{
    var http = new StubHandler();
    var client = new HttpClient(http) { BaseAddress = new Uri("https://shop.test/api/") };
    return (new ShopContext(new RestDataContextOptions<ShopContext>(ShopContext.Model, client)), http);
}

static T Single<T>(IReadOnlyList<T> items)
{
    Check(items.Count == 1, $"one item, got {items.Count}");
    return items[0];
}

static void Check(bool condition, string what)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Check failed: {what}.");
    }
}

internal sealed record NameAndStatus(string Name, ProductStatus Status);

internal sealed class NameAndQuantities
{
    public string Name { get; set; } = "";

    public List<int> Quantities { get; set; } = [];
}

/// <summary>Records request bodies and answers with queued responses.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<string> _responses = new();

    public string LastBody { get; private set; } = "";

    public StubHandler Respond(string body)
    {
        _responses.Enqueue(body);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var body = _responses.TryDequeue(out var response) ? response : throw new InvalidOperationException($"No response queued for {request.RequestUri}.");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

/// <summary>Canned server rows.</summary>
internal static class Rows
{
    public const string Chess = """{"id":1,"name":"Chess","price":20,"status":"Active","version":"00000001-0000-0000-0000-000000000000","createdAt":"2026-01-10T09:30:00","releasedOn":"2026-02-01","categoryId":2,"tags":["sale","new"],"categoryName":"Games"}""";
    public const string Games = """{"id":2,"name":"Games"}""";
    public const string Lines = """[{"id":11,"sku":"C1","quantity":2,"unitPrice":20},{"id":12,"sku":"C2","quantity":1,"unitPrice":5.5}]""";
}

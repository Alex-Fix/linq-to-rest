using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LinqToRest.Client.UnitTests;

/// <summary>
/// The request bodies in tests/golden are the contract both clients must write: the TypeScript client's tests build
/// the same queries and unit of work and compare against the same files. LINQTOREST_UPDATE_GOLDEN=1 rewrites them
/// from this (reference) client.
/// </summary>
public sealed class GoldenTests
{
    private const string Chess = """{"id":1,"name":"Chess","price":20,"status":"Active","version":"00000001-0000-0000-0000-000000000000","createdAt":"2026-01-10T09:30:00Z","categoryId":2,"tags":["sale"]}""";
    private const string Dune = """{"id":2,"name":"Dune","price":12.5,"status":"Active","version":"00000002-0000-0000-0000-000000000000","createdAt":"2026-02-01T14:00:00Z","categoryId":1,"tags":[]}""";
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    public static readonly TheoryData<string> Cases =
    [
        "query-filter-sort-page", "query-lambdas", "query-strings-dates", "query-in-enum-navigation", "query-select",
        "query-include-cursor", "query-count", "batch-unit-of-work",
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Client_writes_the_golden_body(string name)
    {
        var body = await RunAsync(name);

        if (Environment.GetEnvironmentVariable("LINQTOREST_UPDATE_GOLDEN") == "1")
        {
            var pretty = JsonNode.Parse(body)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(GoldenDirectory(), $"{name}.json"), pretty + "\n", Token);
            return;
        }

        var golden = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", $"{name}.json"), Token);
        Assert.Equal(Normalize(golden), Normalize(body));
    }

    private static async Task<string> RunAsync(string name)
    {
        var (shop, http) = Shop.Create();
        ProductStatus[] statuses = [ProductStatus.Draft];
        switch (name)
        {
            case "query-filter-sort-page":
                await shop.Products.Where(p => p.Price > 10 && p.Name.StartsWith('C')).OrderByDescending(p => p.Price).ThenBy(p => p.Id).Skip(2).Take(5).ToListAsync(Token);
                break;
            case "query-lambdas":
                // ReSharper disable once UseCollectionCountProperty — Count() (not .Count) is the case the TypeScript count() mirrors.
                await shop.Products
                    .Where(p => p.Tags.Any(t => t == "sale") && p.Lines.Where(l => l.Quantity > 1).Sum(l => l.Quantity * l.UnitPrice) > 50 && p.Lines.Count() > 1)
                    .Take(10).ToListAsync(Token);
                break;
            case "query-strings-dates":
                await shop.Products
                    .Where(p => p.Name.Equals("go", StringComparison.OrdinalIgnoreCase) && p.Name.Length > 1 && p.CreatedAt.Year == 2026 && p.ReleasedOn != null && (p.Description ?? "") != "x")
                    .Take(10).ToListAsync(Token);
                break;
            case "query-in-enum-navigation":
                // ReSharper disable CSharp14OverloadResolutionWithSpanBreakingChange, NegativeEqualityExpression — the span overload and the not node are the cases under test.
                await shop.Products.Where(p => statuses.Contains(p.Status) && p.Category!.Name == "Games" && !(p.Id == 3)).Take(10).ToListAsync(Token);
                // ReSharper restore CSharp14OverloadResolutionWithSpanBreakingChange, NegativeEqualityExpression
                break;
            case "query-select":
                await shop.Products.Select(p => new
                {
                    p.Id,
                    Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
                    Label = p.Name + " (" + p.CategoryName + ")",
                    Skus = p.Lines.Where(l => l.Quantity > 1).Select(l => l.Sku).ToList(),
                    p.Category,
                    Expensive = p.Price > 18 ? "yes" : "no",
                }).Take(20).ToListAsync(Token);
                break;
            case "query-include-cursor":
                await shop.Products.Include(p => p.Category).Include(p => p.Lines).OrderBy(p => p.Price).After("c1").Take(3).ToListAsync(Token);
                break;
            case "query-count":
                http.Respond("""{"items":[],"totalCount":4}""");
                await shop.Products.CountAsync(p => p.Price > 1, Token);
                break;
            default:
                http.Respond($$"""{"items":[{{Chess}},{{Dune}}]}""");
                var products = await shop.Products.Take(2).ToListAsync(Token);
                products[0].Price = 21;
                var puzzles = new CategoryDto { Name = "Puzzles" };
                shop.Categories.Add(puzzles);
                // Every writable member set: the .NET client sends them all, the TypeScript client sends those present.
                shop.Products.Add(new ProductDto { Name = "Cube", Description = null, Price = 9.5m, Status = ProductStatus.Active, ReleasedOn = null, Category = puzzles });
                shop.Products.Remove(products[1]);
                http.Respond("""{"results":[]}""");
                await shop.SaveChangesAsync(Token);
                break;
        }

        return http.LastBody;
    }

    private static string Normalize(string json) => JsonNode.Parse(json)!.ToJsonString();

    // Only used locally to regenerate the files: CI builds map [CallerFilePath] to /_/.
    private static string GoldenDirectory([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "golden"));
}

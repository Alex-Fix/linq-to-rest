namespace LinqToRest.Client.UnitTests;

/// <summary>Projections read by following the Select lambda instead of the serializer.</summary>
public sealed class ProjectionTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_types_with_nested_collections()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[{"id":1,"label":"Chess!","total":3.5,"lines":[{"sku":"A","quantity":2}],"skus":["A","B"],"tags":["x"]}]}""");

        var rows = await shop.Products
            .Select(p => new
            {
                p.Id,
                Label = p.Name + "!",
                Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
                Lines = p.Lines.Select(l => new { l.Sku, l.Quantity }).ToList(),
                Skus = p.Lines.Select(l => l.Sku).ToArray(),
                p.Tags,
            })
            .Take(1)
            .ToListAsync(Token);

        var row = Assert.Single(rows);
        Assert.Equal((1, "Chess!", 3.5m), (row.Id, row.Label, row.Total));
        Assert.Equal(("A", 2), (row.Lines[0].Sku, row.Lines[0].Quantity));
        Assert.Equal(["A", "B"], row.Skus);
        Assert.Equal(["x"], row.Tags);
    }

    [Fact]
    public async Task Records_classes_and_scalars()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[{"name":"Chess","status":"Active"}]}""")
            .Respond("""{"items":[{"name":"Chess","quantities":[2,3]}]}""")
            .Respond("""{"items":["Chess","Go"]}""");

        var records = await shop.Products.Select(p => new NameAndStatus(p.Name, p.Status)).Take(1).ToListAsync(Token);
        var classes = await shop.Products.Select(p => new NameAndQuantities { Name = p.Name, Quantities = p.Lines.Select(l => l.Quantity).ToList() }).Take(1).ToListAsync(Token);
        var names = await shop.Products.Select(p => p.Name).Take(2).ToListAsync(Token);

        Assert.Equal(new NameAndStatus("Chess", ProductStatus.Active), Assert.Single(records));
        Assert.Equal([2, 3], Assert.Single(classes).Quantities);
        Assert.Equal(["Chess", "Go"], names);
    }

    [Fact]
    public async Task Navigations_and_conversions()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[{"category":{"id":2,"name":"Games"},"none":null,"id":7,"categoryKey":null}]}""");

        var rows = await shop.Products
            .Select(p => new { p.Category, None = p.Category, Id = (double)p.Id, CategoryKey = (int?)p.Category!.Id })
            .Take(1)
            .ToListAsync(Token);

        var row = Assert.Single(rows);
        Assert.Equal("Games", row.Category!.Name);
        Assert.Null(row.None);
        Assert.Equal(7d, row.Id);
        Assert.Null(row.CategoryKey);
    }

    [Fact]
    public async Task A_missing_key_is_named()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[{"id":1}]}""");

        var error = await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => shop.Products.Select(p => new { p.Id, p.Name }).Take(1).ToListAsync(Token));

        Assert.Equal("The response's value at '/items/0' has no 'name'.", error.Message);
    }

    [Fact]
    public async Task Collections_without_ToList_are_refused()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[{"skus":["A"]}]}""");

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => shop.Products.Select(p => new { Skus = p.Lines.Select(l => l.Sku) }).Take(1).ToListAsync(Token));

        Assert.Contains("need ToList() or ToArray()", error.Message, StringComparison.Ordinal);
    }

    public sealed record NameAndStatus(string Name, ProductStatus Status);

    public sealed class NameAndQuantities
    {
        public string Name { get; set; } = "";
        public List<int> Quantities { get; set; } = [];
    }
}

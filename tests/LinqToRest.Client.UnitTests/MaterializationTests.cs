using System.Text.Json;

namespace LinqToRest.Client.UnitTests;

/// <summary>Reading results through the bound delegates instead of the serializer.</summary>
public sealed class MaterializationTests
{
    private const string Chess = """{"id":1,"name":"Chess","price":20,"status":"Active","version":"00000001-0000-0000-0000-000000000000","createdAt":"2026-01-10T09:30:00","releasedOn":"2026-02-01","categoryId":2,"tags":["sale","classic"],"categoryName":"Games"}""";
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rows_read_every_member_kind()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");

        var chess = await shop.Products.FirstAsync(Token);

        Assert.Equal((1, "Chess", 20m, ProductStatus.Active), (chess.Id, chess.Name, chess.Price, chess.Status));
        Assert.Equal(new DateOnly(2026, 2, 1), chess.ReleasedOn);
        Assert.Equal(["sale", "classic"], chess.Tags);
        Assert.Equal(new Guid("00000001-0000-0000-0000-000000000000"), chess.Version);
    }

    [Fact]
    public async Task Keys_the_model_does_not_know_are_ignored()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess[..^1]}},"addedLater":42}]}""");

        var chess = await shop.Products.FirstAsync(Token);

        Assert.Equal("Chess", chess.Name);
    }

    [Fact]
    public async Task Included_navigations_are_read_and_tracked()
    {
        var (shop, http) = Shop.Create();
        var withNavigations = $$"""{{Chess[..^1]}},"category":{"id":2,"name":"Games"},"lines":[{"id":7,"sku":"C-1","quantity":2,"unitPrice":1.5}]}""";
        http.Respond($$"""{"items":[{{withNavigations}}]}""");

        var chess = await shop.Products.Include(p => p.Category).Include(p => p.Lines).FirstAsync(Token);

        Assert.Equal("Games", chess.Category!.Name);
        Assert.Equal(("C-1", 2, 1.5m), (chess.Lines[0].Sku, chess.Lines[0].Quantity, chess.Lines[0].UnitPrice));
        Assert.Equal(EntityState.Unchanged, shop.Entry(chess.Category).State);
    }

    [Fact]
    public async Task A_value_that_does_not_fit_names_the_set_and_member()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess.Replace("\"price\":20", "\"price\":\"cheap\"", StringComparison.Ordinal)}}]}""");

        var error = await Assert.ThrowsAsync<JsonException>(() => shop.Products.FirstAsync(Token));

        Assert.Equal("The response's value at '/items/0/price' doesn't fit 'products.price'.", error.Message);
    }

    [Fact]
    public async Task No_tracking_rows_are_read_but_not_tracked()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");

        var chess = await shop.Products.AsNoTracking().FirstAsync(Token);

        Assert.Equal("Chess", chess.Name);
        Assert.Equal(EntityState.Detached, shop.Entry(chess).State);
    }
}

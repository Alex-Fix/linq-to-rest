using System.Net;
using System.Text.Json;
using LinqToRest.Modeling;

namespace LinqToRest.Client.UnitTests;

/// <summary>Items added to or removed from linkable collections become link / unlink operations.</summary>
public sealed class LinkTrackingTests
{
    private const string Chess = """{"id":1,"name":"Chess","price":20,"status":"Active","version":"00000001-0000-0000-0000-000000000000","createdAt":"2026-01-10T09:30:00","releasedOn":null,"categoryId":2,"tags":["sale"],"categoryName":"Games"}""";
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Links_go_after_updates_and_before_deletes()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        var eco = new LabelDto { Id = 3, Name = "Eco" };
        var gone = new OrderLineDto { Id = 41, Sku = "H1" };
        shop.Labels.Attach(eco);
        shop.OrderLines.Attach(gone);
        chess.Price = 21;
        chess.Labels.Add(eco);
        shop.OrderLines.Remove(gone);
        http.Respond("""{"results":[{"id":"o0","value":{"id":1,"version":"00000002-0000-0000-0000-000000000000"}},{"id":"o1"},{"id":"o2"}]}""");

        Assert.Equal(3, await shop.SaveChangesAsync(Token));

        Assert.Equal(
            """[{"id":"o0","op":"update","set":"products","key":{"id":1},"values":{"price":21},"token":"00000001-0000-0000-0000-000000000000"},{"id":"o1","op":"link","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":3}}},{"id":"o2","op":"delete","set":"orderLines","key":{"id":41}}]""",
            http.LastJson.GetProperty("operations").GetRawText());
        Assert.Equal(EntityState.Unchanged, shop.Entry(chess).State);
        Assert.Equal(0, await shop.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task New_parents_and_items_are_linked_by_ref_and_the_item_points_back()
    {
        var (shop, http) = Shop.Create();
        var puzzles = new CategoryDto { Name = "Puzzles" };
        var cube = new ProductDto { Name = "Cube", Price = 9.5m };
        shop.Categories.Add(puzzles);
        shop.Products.Add(cube);
        puzzles.Products.Add(cube);
        http.Respond("""{"results":[{"id":"o0","value":{"id":7,"name":"Puzzles"}},{"id":"o1","value":{"id":42}},{"id":"o2"}]}""");

        Assert.Equal(3, await shop.SaveChangesAsync(Token));

        var link = http.LastJson.GetProperty("operations")[2];
        Assert.Equal("""{"id":"o2","op":"link","set":"categories","ref":"o0","navigation":"products","item":{"ref":"o1"}}""", link.GetRawText());
        Assert.Equal((7, 7), (cube.CategoryId, puzzles.Id));
        Assert.Same(puzzles, cube.Category);
        Assert.Equal(EntityState.Unchanged, shop.Entry(cube).State);
        Assert.Equal(0, await shop.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task Unlinking_a_required_item_detaches_it_after_the_save()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess[..^1]}},"lines":[{"id":11,"sku":"C1","quantity":2,"unitPrice":20}]}]}""");
        var chess = await shop.Products.Include(p => p.Lines).FirstAsync(Token);
        var line = chess.Lines[0];
        chess.Lines.Remove(line);
        http.Respond("""{"results":[{"id":"o0"}]}""");

        Assert.Equal(1, await shop.SaveChangesAsync(Token));

        Assert.Equal("""{"id":"o0","op":"unlink","set":"products","key":{"id":1},"navigation":"lines","item":{"key":{"id":11}}}""", http.LastJson.GetProperty("operations")[0].GetRawText());
        Assert.Equal(EntityState.Detached, shop.Entry(line).State);
    }

    [Fact]
    public async Task Unlinking_an_optional_item_sends_its_token_and_clears_its_foreign_key()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{"id":2,"name":"Games","products":[{{Chess}}]}]}""");
        var games = await shop.Categories.Include(c => c.Products).FirstAsync(Token);
        var chess = games.Products[0];
        chess.Category = games;
        games.Products.Remove(chess);
        http.Respond("""{"results":[{"id":"o0"}]}""");

        await shop.SaveChangesAsync(Token);

        JsonEqual("""{"key":{"id":1},"token":"00000001-0000-0000-0000-000000000000"}""", http.LastJson.GetProperty("operations")[0].GetProperty("item"));
        Assert.Null(chess.CategoryId);
        Assert.Null(chess.Category);
        Assert.Equal(EntityState.Unchanged, shop.Entry(chess).State);
    }

    [Fact]
    public async Task Deleting_an_item_covers_removing_it()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess[..^1]}},"lines":[{"id":11,"sku":"C1","quantity":2,"unitPrice":20}]}]}""");
        var chess = await shop.Products.Include(p => p.Lines).FirstAsync(Token);
        var line = chess.Lines[0];
        chess.Lines.Remove(line);
        shop.OrderLines.Remove(line);
        http.Respond("""{"results":[{"id":"o0"}]}""");

        Assert.Equal(1, await shop.SaveChangesAsync(Token));

        Assert.Equal("delete", Assert.Single(http.LastJson.GetProperty("operations").EnumerateArray()).GetProperty("op").GetString());
    }

    [Fact]
    public async Task Items_must_be_tracked()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        chess.Labels.Add(new LabelDto { Id = 9, Name = "Stray" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => shop.SaveChangesAsync(Token));

        Assert.Contains("doesn't track. Add or attach it first", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collections_without_links_still_refuse_changes()
    {
        var document = RestModel.Parse(ShopContext.Model.Document.ToJson(indented: false).Replace(",\"links\":\"manyToMany\"", "", StringComparison.Ordinal));
        var http = new StubHandler();
        var model = ClientModel.Bind(document, new Dictionary<string, Type>
        {
            ["bins"] = typeof(BinDto),
            ["categories"] = typeof(CategoryDto),
            ["labels"] = typeof(LabelDto),
            ["orderLines"] = typeof(OrderLineDto),
            ["products"] = typeof(ProductDto),
        });
        var shop = new ShopContext(new RestDataContextOptions<ShopContext>(model, new HttpClient(http) { BaseAddress = new Uri("https://shop.test/api/") }));
        http.Respond($$"""{"items":[{{Chess}}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        var eco = new LabelDto { Id = 3, Name = "Eco" };
        shop.Labels.Attach(eco);
        chess.Labels.Add(eco);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => shop.SaveChangesAsync(Token));

        Assert.Contains("which doesn't allow links", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_link_is_reported_on_its_parent()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        var eco = new LabelDto { Id = 3, Name = "Eco" };
        shop.Labels.Attach(eco);
        chess.Labels.Add(eco);
        http.Problem(HttpStatusCode.NotFound, """{"type":"urn:linqtorest:error:not_found","title":"Not found.","status":404,"code":"not_found","pointer":"/operations/0/item/key","operationId":"o0"}""");

        var error = await Assert.ThrowsAsync<RestSaveException>(() => shop.SaveChangesAsync(Token));

        Assert.Same(chess, Assert.Single(error.Entities));
        Assert.Contains(eco, chess.Labels);
    }

    [Fact]
    public async Task Composite_foreign_keys_are_copied_or_sent_as_refs()
    {
        var (shop, http) = Shop.Create();
        var shelf = new BinDto { Code = "A9" };
        var cube = new ProductDto { Name = "Cube", Bin = shelf };
        var known = new BinDto { Zone = Guid.NewGuid(), Slot = Guid.NewGuid(), Code = "A1" };
        var crate = new ProductDto { Name = "Crate", Bin = known };
        shop.Bins.Add(shelf);
        shop.Bins.Attach(known);
        shop.Products.Add(cube);
        shop.Products.Add(crate);
        http.Respond("""{"results":[]}""");

        await shop.SaveChangesAsync(Token);

        var operations = http.LastJson.GetProperty("operations").EnumerateArray().Where(op => op.GetProperty("set").GetString() == "products").ToList();
        var byName = operations.ToDictionary(op => op.GetProperty("values").GetProperty("name").GetString()!);
        JsonEqual("""{"binZone":"o0","binSlot":"o0"}""", byName["Cube"].GetProperty("refs"));
        Assert.Equal((known.Zone, known.Slot), (byName["Crate"].GetProperty("values").GetProperty("binZone").GetGuid(), byName["Crate"].GetProperty("values").GetProperty("binSlot").GetGuid()));
    }

    private static void JsonEqual(string expected, JsonElement actual) =>
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(expected).RootElement, actual), actual.GetRawText());
}

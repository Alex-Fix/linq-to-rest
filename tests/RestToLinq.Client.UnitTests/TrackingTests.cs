using System.Net;
using System.Text.Json;

namespace RestToLinq.Client.UnitTests;

public sealed class TrackingTests
{
    private const string Chess = """{"id":1,"name":"Chess","price":20,"status":"Active","version":"00000001-0000-0000-0000-000000000000","createdAt":"2026-01-10T09:30:00","releasedOn":null,"categoryId":2,"tags":["sale"],"categoryName":"Games"}""";
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Queried_dtos_are_materialized_and_tracked_by_key()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""").Respond($$"""{"items":[{{Chess.Replace("Chess", "Other", StringComparison.Ordinal)}}]}""");

        var first = await shop.Products.FirstAsync(Token);
        var again = await shop.Products.FirstAsync(Token);

        Assert.Same(first, again);
        Assert.Equal(("Chess", 20m, ProductStatus.Active, 2), (first.Name, first.Price, first.Status, first.CategoryId));
        Assert.Equal(["sale"], first.Tags);
        Assert.Equal(EntityState.Unchanged, shop.Entry(first).State);
    }

    [Fact]
    public async Task No_tracking_and_projections_are_not_tracked()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""").Respond($$"""{"items":[{{Chess}}]}""").Respond("""{"items":[{"id":1,"label":"Chess!"}]}""");

        var first = await shop.Products.AsNoTracking().FirstAsync(Token);
        var second = await shop.Products.AsNoTracking().FirstAsync(Token);
        var projected = await shop.Products.Select(p => new { p.Id, Label = p.Name + "!" }).FirstAsync(Token);

        Assert.NotSame(first, second);
        Assert.Equal(EntityState.Detached, shop.Entry(first).State);
        Assert.Equal((1, "Chess!"), (projected.Id, projected.Label));
    }

    [Fact]
    public async Task Included_navigations_share_tracked_instances()
    {
        var (shop, http) = Shop.Create();
        var withCategory = Chess.Replace("}", ""","category":{"id":2,"name":"Games"},"lines":[{"id":11,"sku":"C1","quantity":2,"unitPrice":20}]}""", StringComparison.Ordinal);
        http.Respond("""{"items":[{"id":2,"name":"Games"}]}""").Respond($$"""{"items":[{{withCategory}}]}""");

        var games = await shop.Categories.FirstAsync(Token);
        var chess = await shop.Products.Include(p => p.Category).Include(p => p.Lines).FirstAsync(Token);

        Assert.Same(games, chess.Category);
        Assert.Equal("C1", Assert.Single(chess.Lines).Sku);
        Assert.Equal(EntityState.Unchanged, shop.Entry(chess.Lines[0]).State);
    }

    [Fact]
    public async Task Changed_members_are_saved_as_an_update_and_refreshed_from_the_result()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        chess.Price = 21;
        chess.CategoryName = "ignored, read-only";
        http.Respond($$"""{"results":[{"id":"o0","value":{{Chess.Replace("\"price\":20", "\"price\":21", StringComparison.Ordinal).Replace("00000001-", "00000002-", StringComparison.Ordinal)}}}]}""");

        Assert.Equal(EntityState.Modified, shop.Entry(chess).State);
        Assert.True(shop.Entry(chess).Property(p => p.Price).IsModified);
        Assert.Equal(1, await shop.SaveChangesAsync(Token));

        Assert.Equal((HttpMethod.Post, "/api/$batch"), (http.Requests[^1].Method, http.Requests[^1].Path));
        JsonEqual("""
            {"v":1,"operations":[{"id":"o0","op":"update","set":"products","key":{"id":1},"values":{"price":21},"token":"00000001-0000-0000-0000-000000000000"}]}
            """, http.LastJson);
        Assert.Equal(new Guid("00000002-0000-0000-0000-000000000000"), chess.Version);
        Assert.Equal("Games", chess.CategoryName);
        Assert.Equal(EntityState.Unchanged, shop.Entry(chess).State);
        Assert.Equal(0, await shop.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task Added_dtos_are_inserted_in_reference_order_with_refs()
    {
        var (shop, http) = Shop.Create();
        var product = new ProductDto { Name = "Cube", Price = 9.5m };
        var category = new CategoryDto { Name = "Puzzles" };
        product.Category = category;
        shop.Products.Add(product);
        shop.Categories.Add(category);
        http.Respond("""
            {"results":[
              {"id":"o0","value":{"id":7,"name":"Puzzles"}},
              {"id":"o1","value":{"id":42,"name":"Cube","price":9.5,"status":"Draft","version":"00000009-0000-0000-0000-000000000000","createdAt":"2026-10-18T12:00:00","categoryId":7,"tags":[],"categoryName":"Puzzles"}}]}
            """);

        Assert.Equal(2, await shop.SaveChangesAsync(Token));

        var operations = http.LastJson.GetProperty("operations");
        Assert.Equal(["categories", "products"], operations.EnumerateArray().Select(op => op.GetProperty("set").GetString()));
        JsonEqual("""{"name":"Puzzles"}""", operations[0].GetProperty("values"));
        JsonEqual("""{"categoryId":"o0"}""", operations[1].GetProperty("refs"));
        Assert.False(operations[1].GetProperty("values").TryGetProperty("categoryId", out _));
        Assert.False(operations[1].TryGetProperty("key", out _));
        Assert.Equal((42, 7, 7), (product.Id, product.CategoryId, category.Id));
        Assert.Equal(EntityState.Unchanged, shop.Entry(product).State);
    }

    [Fact]
    public async Task Assigning_a_tracked_navigation_sets_the_foreign_key()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""").Respond("""{"items":[{"id":3,"name":"Tools"}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        chess.Category = await shop.Categories.FirstAsync(Token);
        http.Respond($$"""{"results":[{"id":"o0","value":{{Chess}}}]}""");

        await shop.SaveChangesAsync(Token);

        JsonEqual("""{"categoryId":3}""", http.LastJson.GetProperty("operations")[0].GetProperty("values"));
    }

    [Fact]
    public async Task Removed_dtos_are_deleted_and_detached()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        shop.Products.Remove(chess);
        http.Respond("""{"results":[{"id":"o0"}]}""");

        await shop.SaveChangesAsync(Token);

        JsonEqual("""{"id":"o0","op":"delete","set":"products","key":{"id":1},"token":"00000001-0000-0000-0000-000000000000"}""", http.LastJson.GetProperty("operations")[0]);
        Assert.Equal(EntityState.Detached, shop.Entry(chess).State);
    }

    [Fact]
    public async Task Removing_an_added_dto_forgets_it()
    {
        var (shop, http) = Shop.Create();
        var category = new CategoryDto { Name = "Temp" };
        shop.Categories.Add(category);
        shop.Categories.Remove(category);

        Assert.Equal(0, await shop.SaveChangesAsync(Token));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Update_sends_every_writable_member()
    {
        var (shop, http) = Shop.Create();
        var category = new CategoryDto { Id = 2, Name = "Games" };
        shop.Categories.Update(category);
        http.Respond("""{"results":[{"id":"o0","value":{"id":2,"name":"Games"}}]}""");

        await shop.SaveChangesAsync(Token);

        JsonEqual("""{"id":"o0","op":"update","set":"categories","key":{"id":2},"values":{"name":"Games"}}""", http.LastJson.GetProperty("operations")[0]);
    }

    [Fact]
    public async Task Is_modified_can_be_reset()
    {
        var (shop, http) = Shop.Create();
        var category = new CategoryDto { Id = 2, Name = "Games" };
        shop.Categories.Attach(category);
        category.Name = "Board games";
        shop.Entry(category).Property(c => c.Name).IsModified = false;

        Assert.Equal(EntityState.Unchanged, shop.Entry(category).State);
        Assert.Equal(0, await shop.SaveChangesAsync(Token));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Rejected_save_keeps_the_tracked_state()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        chess.Price = 1;
        http.Problem(HttpStatusCode.PreconditionFailed, """{"status":412,"code":"concurrency_conflict","pointer":"","operationId":"o0"}""");

        var error = await Assert.ThrowsAsync<RestConcurrencyException>(() => shop.SaveChangesAsync(Token));

        Assert.Equal(("concurrency_conflict", 412), (error.Code, error.Status));
        Assert.Same(chess, Assert.Single(error.Entities));
        Assert.Equal(EntityState.Modified, shop.Entry(chess).State);
    }

    [Fact]
    public async Task Changing_collection_membership_is_not_supported()
    {
        var (shop, http) = Shop.Create();
        http.Respond($$"""{"items":[{{Chess}}]}""");
        var chess = await shop.Products.FirstAsync(Token);
        chess.Lines.Add(new OrderLineDto { Sku = "X" });

        await Assert.ThrowsAsync<NotSupportedException>(() => shop.SaveChangesAsync(Token));
    }

    [Fact]
    public void Navigation_to_an_untracked_dto_must_be_added_first()
    {
        var (shop, _) = Shop.Create();
        var product = new ProductDto { Name = "Cube", Category = new CategoryDto { Id = 5 } };
        shop.Products.Add(product);

        Assert.Throws<InvalidOperationException>(() => shop.Entry(product).State);
    }

    [Fact]
    public void Attaching_a_second_instance_with_the_same_key_fails()
    {
        var (shop, _) = Shop.Create();
        shop.Categories.Attach(new CategoryDto { Id = 1 });

        Assert.Throws<InvalidOperationException>(() => shop.Categories.Attach(new CategoryDto { Id = 1 }));
    }

    private static void JsonEqual(string expected, JsonElement actual)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual), $"Expected {expected}{Environment.NewLine}Actual   {actual.GetRawText()}");
    }
}

using LinqToRest.Client;
using LinqToRest.Modeling;
using LinqToRest.Server.IntegrationTests.Shop;

namespace LinqToRest.EndToEnd.Tests;

public abstract class EndToEndTests(ShopDatabase database) : IAsyncLifetime
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private ShopHost? _started;

    /// <summary>Gets the host, skipping the calling test when the provider isn't available.</summary>
    private ShopHost Host
    {
        get
        {
            database.SkipIfUnavailable();
            return _started!;
        }
    }

    public async ValueTask InitializeAsync() => _started = database.SkipReason is null ? await ShopHost.StartAsync(database) : null;

    public async ValueTask DisposeAsync()
    {
        if (_started is not null)
        {
            await _started.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Linq_filters_sorts_and_pages_on_the_server()
    {
        var shop = Host.Client();
        var minimum = 12m;
        int[] excluded = [3];

        // ReSharper disable once CSharp14OverloadResolutionWithSpanBreakingChange — users write this; it binds to the span overload the translator must handle.
        var names = await shop.Products
            .Where(p => p.Price >= minimum && !excluded.Contains(p.Id) && p.Status != ProductStatus.Retired)
            .Where(p => p.Tags.Any(t => t == "sale") || p.Category!.Name == "Books")
            .OrderByDescending(p => p.Price)
            .ThenBy(p => p.Id)
            .Select(p => p.Name)
            .Take(10)
            .ToListAsync(Token);

        Assert.Equal(["Chess", "Dune"], names);
    }

    [Fact]
    public async Task Projections_compute_on_the_server()
    {
        var shop = Host.Client();

        var chess = await shop.Products
            .Where(p => p.Id == 1)
            .Select(p => new
            {
                p.Name,
                Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
                Label = p.Name + " (" + p.Category!.Name + ")",
                Bulk = p.Lines.Where(l => l.Quantity > 1).Select(l => l.Sku).ToList(),
                LineCount = p.Lines.Count,
                p.Category,
            })
            .SingleAsync(Token);

        Assert.Equal(("Chess", 58m, "Chess (Games)", 2), (chess.Name, chess.Total, chess.Label, chess.LineCount));
        Assert.Equal(["C1"], chess.Bulk);
        Assert.Equal("Games", chess.Category!.Name);
    }

    [Fact]
    public async Task Cursor_pages_and_streaming_return_every_visible_row()
    {
        var shop = Host.Client();
        var seen = new List<int>();
        var page = await shop.Products.OrderBy(p => p.Price).Take(2).ToCursorPageAsync(includeTotalCount: true, Token);
        Assert.Equal(5, page.TotalCount);
        seen.AddRange(page.Items.Select(p => p.Id));
        while (page.NextCursor is { } cursor)
        {
            page = await shop.Products.OrderBy(p => p.Price).After(cursor).Take(2).ToCursorPageAsync(cancellationToken: Token);
            seen.AddRange(page.Items.Select(p => p.Id));
        }

        var streamed = new List<int>();
        await foreach (var product in shop.Products.OrderBy(p => p.Price).AsAsyncEnumerable(Token))
        {
            streamed.Add(product.Id);
        }

        // Tenant 1 can't see tenant 2's Hammer (id 4).
        Assert.Equal([5, 2, 1, 6, 3], seen);
        Assert.Equal(seen, streamed);
    }

    [Fact]
    public async Task Count_any_first_and_single()
    {
        var shop = Host.Client();

        Assert.Equal(5, await shop.Products.CountAsync(Token));
        Assert.Equal(3, await shop.Products.CountAsync(p => p.Category!.Name == "Games", Token));
        Assert.True(await shop.Products.AnyAsync(p => p.Name == "Go", Token));
        Assert.False(await shop.Products.AnyAsync(p => p.Name == "Hammer", Token));
        Assert.Equal("Dune", (await shop.Products.OrderBy(p => p.Price).Skip(1).FirstAsync(Token)).Name);
        Assert.Null(await shop.Products.SingleOrDefaultAsync(p => p.Id == 4, Token));
    }

    [Fact]
    public async Task Query_modify_save_and_query_again()
    {
        var shop = Host.Client();
        var chess = await shop.Products.Include(p => p.Category).FirstAsync(p => p.Id == 1, Token);
        var oldVersion = chess.Version;
        chess.Price = 25;
        chess.Category = await shop.Categories.FirstAsync(c => c.Name == "Books", Token);

        Assert.Equal(1, await shop.SaveChangesAsync(Token));

        Assert.NotEqual(oldVersion, chess.Version);
        Assert.Equal(("Books", 1), (chess.CategoryName, chess.CategoryId));
        var fresh = await Host.Client().Products.FirstAsync(p => p.Id == 1, Token);
        Assert.Equal((25m, "Books", chess.Version), (fresh.Price, fresh.CategoryName, fresh.Version));
    }

    [Fact]
    public async Task New_dtos_get_generated_keys_and_their_references()
    {
        var shop = Host.Client();
        var category = new Category { Name = "Puzzles" };
        var cube = new Product { Name = "Cube", Price = 9.5m, Status = ProductStatus.Active, Category = category };
        shop.Categories.Add(category);
        shop.Products.Add(cube);

        await shop.SaveChangesAsync(Token);

        Assert.True(category.Id > 0);
        Assert.Equal(category.Id, cube.CategoryId);
        var loaded = await Host.Client().Products.Include(p => p.Category).SingleAsync(p => p.Id == cube.Id, Token);
        Assert.Equal(("Cube", "Puzzles", new DateTime(2026, 10, 18, 12, 0, 0)), (loaded.Name, loaded.Category!.Name, loaded.CreatedAt));
    }

    [Fact]
    public async Task Removed_dtos_are_deleted()
    {
        var shop = Host.Client();
        shop.Products.Remove(await shop.Products.FirstAsync(p => p.Id == 2, Token));

        await shop.SaveChangesAsync(Token);

        Assert.False(await Host.Client().Products.AnyAsync(p => p.Id == 2, Token));
    }

    [Fact]
    public async Task Concurrent_edit_is_rejected_and_can_be_retried_after_reloading()
    {
        var first = Host.Client();
        var second = Host.Client();
        var mine = await first.Products.FirstAsync(p => p.Id == 3, Token);
        var theirs = await second.Products.FirstAsync(p => p.Id == 3, Token);
        theirs.Price = 31;
        await second.SaveChangesAsync(Token);
        mine.Price = 32;

        var error = await Assert.ThrowsAsync<RestConcurrencyException>(() => first.SaveChangesAsync(Token));

        Assert.Same(mine, Assert.Single(error.Entities));
        var retry = Host.Client();
        var reloaded = await retry.Products.FirstAsync(p => p.Id == 3, Token);
        reloaded.Price = 32;
        await retry.SaveChangesAsync(Token);
        Assert.Equal(32m, (await Host.Client().Products.FirstAsync(p => p.Id == 3, Token)).Price);
    }

    [Fact]
    public async Task Rejected_query_surfaces_as_a_typed_exception()
    {
        var error = await Assert.ThrowsAsync<RestQueryException>(() => Host.Client().Products.Take(21).ToListAsync(Token));

        Assert.Equal((400, "limit_exceeded", "pageSize", "/page/take"), (error.Status, error.Code, error.Limit, error.Location));
    }

    [Fact]
    public async Task Rows_of_another_tenant_are_not_found()
    {
        var otherTenant = Host.Client(tenant: 2);
        otherTenant.Products.Update(new Product { Id = 1, Name = "Mine now", Version = new Guid("00000001-0000-0000-0000-000000000000") });

        var error = await Assert.ThrowsAsync<RestSaveException>(() => otherTenant.SaveChangesAsync(Token));

        Assert.Equal((404, "not_found"), (error.Status, error.Code));
        Assert.Equal("Chess", (await Host.Client().Products.FirstAsync(p => p.Id == 1, Token)).Name);
    }

    [Fact]
    public async Task Denied_writes_are_forbidden()
    {
        var readOnly = Host.Client(readOnly: true);
        readOnly.Products.Add(new Product { Name = "Nope" });

        var error = await Assert.ThrowsAsync<RestSaveException>(() => readOnly.SaveChangesAsync(Token));

        Assert.Equal((403, "forbidden"), (error.Status, error.Code));
    }

    [Fact]
    public async Task Writes_the_set_does_not_allow_fail_before_and_on_the_server()
    {
        var client = Host.Client();
        var games = await client.Categories.FirstAsync(c => c.Name == "Games", Token);

        // The generated model knows categories can't be deleted, so nothing is sent.
        var local = Assert.Throws<InvalidOperationException>(() => client.Categories.Remove(games));

        // A client built from an outdated model sends the delete; the server refuses it the same way.
        var outdated = Host.Client(model: ClientModel.Bind(
            RestModel.Parse(ShopClient.Model.Document.ToJson(indented: false).Replace("\"name\":\"categories\",\"type\":\"Category\",\"key\":[\"id\"],\"writes\":[\"insert\",\"update\"]", "\"name\":\"categories\",\"type\":\"Category\",\"key\":[\"id\"],\"writes\":[\"insert\",\"update\",\"delete\"]", StringComparison.Ordinal)),
            new Dictionary<string, Type> { ["categories"] = typeof(Category), ["orderLines"] = typeof(OrderLine), ["products"] = typeof(Product) }));
        var stale = await outdated.Categories.FirstAsync(c => c.Name == "Games", Token);
        outdated.Categories.Remove(stale);
        var remote = await Assert.ThrowsAsync<RestSaveException>(() => outdated.SaveChangesAsync(Token));

        Assert.Equal("The set 'categories' doesn't allow deletes.", local.Message);
        Assert.Equal((403, "operation_not_allowed", "/operations/0"), (remote.Status, remote.Code, remote.Location));
        Assert.Same(stale, Assert.Single(remote.Entities));
        Assert.Equal("Games", (await Host.Client().Categories.FirstAsync(c => c.Id == games.Id, Token)).Name);
    }

    [Fact]
    public async Task Groups_and_whole_query_aggregates_come_from_the_server()
    {
        var shop = Host.Client();

        var facets = await shop.Products
            .Where(p => p.Status != ProductStatus.Retired)
            .GroupBy(p => new { p.CategoryId, p.Category!.Name })
            .Select(g => new { g.Key.CategoryId, g.Key.Name, Count = g.Count(), Cheapest = g.Min(p => p.Price), Rating = g.Average(p => p.Rating) })
            .Where(f => f.Count > 0)
            .OrderByDescending(f => f.Count)
            .ToListAsync(Token);
        var total = await shop.Products.SumAsync(p => p.Price, Token);
        var best = await shop.Products.MaxAsync(p => p.Rating, Token);
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => shop.Products.Where(p => p.Price < 0).AverageAsync(p => p.Rating, Token));

        // Tenant 1 only: the policy filter applies before grouping.
        Assert.Equal(
            [((int?)2, "Games", 3, 20m, 4.2), (1, "Books", 1, 12.5m, 4.8)],
            facets.Select(f => (f.CategoryId, f.Name, f.Count, f.Cheapest, Math.Round(f.Rating, 2))));
        Assert.Equal((85.5m, 4.8), (total, best));
        Assert.Contains("no rows", none.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rules_fail_before_sending_and_on_the_server_with_the_same_errors()
    {
        var client = Host.Client();
        var local = new Product { Name = new string('x', 101), Price = -1 };
        client.Products.Add(local);

        var before = await Assert.ThrowsAsync<RestValidationException>(() => client.SaveChangesAsync(Token));

        // A client built from a model without the rules sends the insert; the server finds the same errors.
        var outdated = Host.Client(model: ClientModel.Bind(
            RestModel.Parse(ShopClient.Model.Document.ToJson(indented: false)
                .Replace(",\"rules\":{\"maxLength\":100}", "", StringComparison.Ordinal)
                .Replace(",\"rules\":{\"min\":0}", "", StringComparison.Ordinal)),
            new Dictionary<string, Type> { ["categories"] = typeof(Category), ["orderLines"] = typeof(OrderLine), ["products"] = typeof(Product) }));
        var remote = new Product { Name = new string('x', 101), Price = -1 };
        outdated.Products.Add(remote);
        var after = await Assert.ThrowsAsync<RestValidationException>(() => outdated.SaveChangesAsync(Token));

        Assert.True(before.IsLocal);
        Assert.False(after.IsLocal);
        Assert.Equal(422, after.Status);
        Assert.Equal([("Name", "too_long"), ("Price", "out_of_range")], before.Errors.Select(error => (error.Member, error.Code)));
        Assert.Equal(before.Errors.Select(error => (error.Member, error.Code)), after.Errors.Select(error => (error.Member, error.Code)));
        Assert.All(after.Errors, error => Assert.Same(remote, error.Entity));
        Assert.Equal(EntityState.Added, outdated.Entry(remote).State);
    }
}

public sealed class SqliteEndToEndTests() : EndToEndTests(new SqliteDatabase());

public sealed class PostgresEndToEndTests(PostgresContainer postgres) : EndToEndTests(new PostgresDatabase(postgres));

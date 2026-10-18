using RestToLinq.Client;
using RestToLinq.Server.IntegrationTests.Shop;

namespace RestToLinq.EndToEnd.Tests;

public sealed class EndToEndTests : IAsyncLifetime
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;
    private ShopHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await ShopHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Linq_filters_sorts_and_pages_on_the_server()
    {
        var shop = _host.Client();
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
        var shop = _host.Client();

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
        var shop = _host.Client();
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
        var shop = _host.Client();

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
        var shop = _host.Client();
        var chess = await shop.Products.Include(p => p.Category).FirstAsync(p => p.Id == 1, Token);
        var oldVersion = chess.Version;
        chess.Price = 25;
        chess.Category = await shop.Categories.FirstAsync(c => c.Name == "Books", Token);

        Assert.Equal(1, await shop.SaveChangesAsync(Token));

        Assert.NotEqual(oldVersion, chess.Version);
        Assert.Equal(("Books", 1), (chess.CategoryName, chess.CategoryId));
        var fresh = await _host.Client().Products.FirstAsync(p => p.Id == 1, Token);
        Assert.Equal((25m, "Books", chess.Version), (fresh.Price, fresh.CategoryName, fresh.Version));
    }

    [Fact]
    public async Task New_dtos_get_generated_keys_and_their_references()
    {
        var shop = _host.Client();
        var category = new CategoryDto { Name = "Puzzles" };
        var cube = new ProductDto { Name = "Cube", Price = 9.5m, Status = ProductStatus.Active, Category = category };
        shop.Categories.Add(category);
        shop.Products.Add(cube);

        await shop.SaveChangesAsync(Token);

        Assert.True(category.Id > 0);
        Assert.Equal(category.Id, cube.CategoryId);
        var loaded = await _host.Client().Products.Include(p => p.Category).SingleAsync(p => p.Id == cube.Id, Token);
        Assert.Equal(("Cube", "Puzzles", new DateTime(2026, 10, 18, 12, 0, 0)), (loaded.Name, loaded.Category!.Name, loaded.CreatedAt));
    }

    [Fact]
    public async Task Removed_dtos_are_deleted()
    {
        var shop = _host.Client();
        shop.Products.Remove(await shop.Products.FirstAsync(p => p.Id == 2, Token));

        await shop.SaveChangesAsync(Token);

        Assert.False(await _host.Client().Products.AnyAsync(p => p.Id == 2, Token));
    }

    [Fact]
    public async Task Concurrent_edit_is_rejected_and_can_be_retried_after_reloading()
    {
        var first = _host.Client();
        var second = _host.Client();
        var mine = await first.Products.FirstAsync(p => p.Id == 3, Token);
        var theirs = await second.Products.FirstAsync(p => p.Id == 3, Token);
        theirs.Price = 31;
        await second.SaveChangesAsync(Token);
        mine.Price = 32;

        var error = await Assert.ThrowsAsync<RestConcurrencyException>(() => first.SaveChangesAsync(Token));

        Assert.Same(mine, Assert.Single(error.Entities));
        var retry = _host.Client();
        var reloaded = await retry.Products.FirstAsync(p => p.Id == 3, Token);
        reloaded.Price = 32;
        await retry.SaveChangesAsync(Token);
        Assert.Equal(32m, (await _host.Client().Products.FirstAsync(p => p.Id == 3, Token)).Price);
    }

    [Fact]
    public async Task Rejected_query_surfaces_as_a_typed_exception()
    {
        var error = await Assert.ThrowsAsync<RestQueryException>(() => _host.Client().Products.Take(21).ToListAsync(Token));

        Assert.Equal((400, "limit_exceeded", "pageSize", "/page/take"), (error.Status, error.Code, error.Limit, error.Location));
    }

    [Fact]
    public async Task Rows_of_another_tenant_are_not_found()
    {
        var otherTenant = _host.Client(tenant: 2);
        otherTenant.Products.Update(new ProductDto { Id = 1, Name = "Mine now", Version = new Guid("00000001-0000-0000-0000-000000000000") });

        var error = await Assert.ThrowsAsync<RestSaveException>(() => otherTenant.SaveChangesAsync(Token));

        Assert.Equal((404, "not_found"), (error.Status, error.Code));
        Assert.Equal("Chess", (await _host.Client().Products.FirstAsync(p => p.Id == 1, Token)).Name);
    }

    [Fact]
    public async Task Denied_writes_are_forbidden()
    {
        var readOnly = _host.Client(readOnly: true);
        readOnly.Products.Add(new ProductDto { Name = "Nope" });

        var error = await Assert.ThrowsAsync<RestSaveException>(() => readOnly.SaveChangesAsync(Token));

        Assert.Equal((403, "forbidden"), (error.Status, error.Code));
    }
}

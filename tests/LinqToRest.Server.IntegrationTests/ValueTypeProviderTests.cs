using System.Collections.Concurrent;
using LinqToRest.Server.IntegrationTests.Shop;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>
/// What value types rely on in EF Core itself, per mapping (required and optional complex types, table-split and JSON
/// owned types): member access, null tests and whole-value projections run in SQL, and assigning a new value updates
/// the row in place.
/// </summary>
public abstract class ValueTypeProviderTests(ShopDatabase database)
{
    [Fact]
    public async Task Members_of_required_and_nested_complex_types_filter_and_sort_in_sql()
    {
        await using var shop = await ShopDb.OpenAsync(database);

        var names = await shop.Db.Products
            .Where(p => p.Size.Weight.Amount > 1 && p.Size.Weight.Unit == "kg")
            .OrderByDescending(p => p.Size.Width)
            .Select(p => p.Name)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Go", "Chess"], names);
    }

    [Fact]
    public async Task Optional_complex_type_null_tests_and_projection_run_in_sql()
    {
        await using var shop = await ShopDb.OpenAsync(database);

        var rows = await shop.Db.Products
            .Where(p => p.Origin != null && p.Origin.City == null)
            .OrderBy(p => p.Id)
            .Select(p => new object?[] { p.Id, p.Origin == null ? null : new object?[] { p.Origin.Country, p.Origin.City } })
            .ToListAsync(TestContext.Current.CancellationToken);
        var none = await shop.Db.Products.CountAsync(p => p.Origin == null, TestContext.Current.CancellationToken);

        Assert.Equal([1, 3], rows.Select(r => (int)r[0]!));
        Assert.Equal(new object?[] { "IN", null }, (object?[])rows[0][1]!);
        Assert.Equal(3, none);
        AssertNullTestInSql(shop);
    }

    [Fact]
    public async Task Optional_owned_type_null_tests_and_projection_run_in_sql()
    {
        await using var shop = await ShopDb.OpenAsync(database);

        var rows = await shop.Db.Categories
            .OrderBy(c => c.Id)
            .Select(c => new object?[] { c.Id, c.Contact == null ? null : new object?[] { c.Contact.Email, c.Contact.Phone } })
            .ToListAsync(TestContext.Current.CancellationToken);
        var withContact = await shop.Db.Categories.CountAsync(c => c.Contact != null && c.Contact.Email.EndsWith("@shop.example"), TestContext.Current.CancellationToken);

        Assert.Null(rows[0][1]);
        Assert.Equal(new object?[] { "games@shop.example", "+380 44 000" }, (object?[])rows[1][1]!);
        Assert.Equal(2, withContact);
        AssertNullTestInSql(shop);
    }

    [Fact]
    public async Task Json_owned_type_null_tests_and_projection_run_in_sql()
    {
        await using var shop = await ShopDb.OpenAsync(database);

        var rows = await shop.Db.Set<OrderLine>()
            .Where(l => l.Shipping != null && l.Shipping.Country != "US")
            .OrderBy(l => l.Shipping!.Country)
            .Select(l => new object?[] { l.Id, l.Shipping == null ? null : new object?[] { l.Shipping.Country, l.Shipping.City } })
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([21, 11], rows.Select(r => (int)r[0]!));
        Assert.Equal(new object?[] { "UA", "Lviv" }, (object?[])rows[1][1]!);
        AssertNullTestInSql(shop);
    }

    [Fact]
    public async Task Complex_values_are_replaced_in_place()
    {
        await using var shop = await ShopDb.OpenAsync(database);
        var chess = await shop.Db.Products.SingleAsync(p => p.Id == 1, TestContext.Current.CancellationToken);
        var dune = await shop.Db.Products.SingleAsync(p => p.Id == 2, TestContext.Current.CancellationToken);
        var hammer = await shop.Db.Products.SingleAsync(p => p.Id == 4, TestContext.Current.CancellationToken);

        chess.Size = new Dimensions { Width = 41, Height = 40, Weight = new Weight(1.3, "kg") };
        dune.Origin = null;
        hammer.Origin = new Place("DE", "Berlin");
        await shop.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var fresh = shop.NewContext();
        var saved = await fresh.Products.Where(p => p.Id == 1 || p.Id == 2 || p.Id == 4).OrderBy(p => p.Id)
            .Select(p => new { p.Size, p.Origin }).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Dimensions { Width = 41, Height = 40, Weight = new Weight(1.3, "kg") }, saved[0].Size);
        Assert.Null(saved[1].Origin);
        Assert.Equal(new Place("DE", "Berlin"), saved[2].Origin);
        Assert.Equal(6, await fresh.Products.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Owned_values_are_replaced_in_place()
    {
        await using var shop = await ShopDb.OpenAsync(database);
        var categories = await shop.Db.Categories.OrderBy(c => c.Id).ToListAsync(TestContext.Current.CancellationToken);
        var lines = await shop.Db.Set<OrderLine>().Where(l => l.Id == 11 || l.Id == 12).OrderBy(l => l.Id).ToListAsync(TestContext.Current.CancellationToken);

        categories[0].Contact = new Contact { Email = "books@shop.example" };
        categories[1].Contact = new Contact { Email = "play@shop.example", Phone = null };
        categories[2].Contact = null;
        lines[0].Shipping = null;
        lines[1].Shipping = new Place("UA", "Kyiv");
        await shop.Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var fresh = shop.NewContext();
        var contacts = await fresh.Categories.AsNoTracking().OrderBy(c => c.Id).Select(c => c.Contact).ToListAsync(TestContext.Current.CancellationToken);
        var shipping = await fresh.Set<OrderLine>().AsNoTracking().Where(l => l.Id == 11 || l.Id == 12).OrderBy(l => l.Id).Select(l => l.Shipping).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["books@shop.example", "play@shop.example", null], contacts.Select(c => c?.Email));
        Assert.Null(contacts[1]!.Phone);
        Assert.Equal([null, new Place("UA", "Kyiv")], shipping);
        Assert.Equal(3, await fresh.Categories.CountAsync(TestContext.Current.CancellationToken));
    }

    private static void AssertNullTestInSql(ShopDb shop) =>
        Assert.Contains(shop.Sql, sql => sql.Contains("IS NULL", StringComparison.OrdinalIgnoreCase) || sql.Contains("IS NOT NULL", StringComparison.OrdinalIgnoreCase));

    /// <summary>A seeded shop database used through EF Core directly, logging its SQL.</summary>
    private sealed class ShopDb : IAsyncDisposable
    {
        private readonly DatabaseLease _lease;

        private ShopDb(DatabaseLease lease)
        {
            _lease = lease;
            Db = NewContext();
        }

        public ShopDbContext Db { get; }

        public ConcurrentQueue<string> Sql { get; } = new();

        public static async Task<ShopDb> OpenAsync(ShopDatabase database)
        {
            database.SkipIfUnavailable();
            var shop = new ShopDb(await database.CreateAsync());
            await shop.Db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            ShopData.Seed(shop.Db);
            shop.Db.ChangeTracker.Clear();
            shop.Sql.Clear();
            return shop;
        }

        public ShopDbContext NewContext()
        {
            var options = new DbContextOptionsBuilder<ShopDbContext>();
            _lease.Configure(options);
            options.LogTo(Sql.Enqueue, [RelationalEventId.CommandExecuted]);
            return new ShopDbContext(options.Options);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (_lease.Owned is not null)
            {
                await _lease.Owned.DisposeAsync();
            }
        }
    }
}

public sealed class SqliteValueTypeProviderTests() : ValueTypeProviderTests(new SqliteDatabase());

public sealed class PostgresValueTypeProviderTests(PostgresContainer postgres) : ValueTypeProviderTests(new PostgresDatabase(postgres));

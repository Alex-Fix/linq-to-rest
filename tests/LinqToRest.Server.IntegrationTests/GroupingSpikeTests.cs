using LinqToRest.Server.IntegrationTests.Shop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>
/// Step 1 of the grouping design: the shape the server will build (keys in our own generic class via MemberInit,
/// aggregates over the group, the flat object[] row) must translate to GROUP BY on every provider.
/// </summary>
public abstract class GroupingSpikeTests(ShopDatabase database) : IAsyncLifetime
{
    private ShopServer? _started;

    public async ValueTask InitializeAsync() => _started = database.SkipReason is null ? await ShopServer.StartAsync(database) : null;

    public async ValueTask DisposeAsync()
    {
        if (_started is not null)
        {
            await _started.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private ShopServer Server
    {
        get
        {
            database.SkipIfUnavailable();
            return _started!;
        }
    }

    [Fact]
    public async Task Member_init_keys_and_group_aggregates_translate()
    {
        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        Server.Sql.Clear();

        var rows = await db.Products
            .Where(p => p.TenantId == 1)
            .GroupBy(p => new SpikeKey<int?, string?> { K0 = p.CategoryId, K1 = p.Category!.Name })
            .Where(g => g.Sum(p => p.Rating) > 0)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.K0).ThenBy(g => g.Key.K1)
            .Skip(0).Take(10)
            .Select(g => new object?[]
            {
                g.Key.K0, g.Key.K1, g.Count(), g.Count(p => p.Rating > 4.2), g.Min(p => p.Price), g.Max(p => p.CreatedAt),
                g.Sum(p => p.Price * 2), g.Average(p => p.Rating),
            })
            .ToListAsync(TestContext.Current.CancellationToken);

        // Providers order a null key differently (SQLite first, PostgreSQL last), so only the first group is fixed.
        Assert.Equal([null, 1, 2], rows.Select(row => (int?)row[0]).Order());
        Assert.Equal((2, "Games", 3, 1), ((int?)rows[0][0], (string?)rows[0][1], (int)rows[0][2]!, (int)rows[0][3]!));
        Assert.Equal(20m, Convert.ToDecimal(rows[0][4], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(140m, Convert.ToDecimal(rows[0][6], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(Server.Sql, sql => sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Whole_query_aggregates_translate()
    {
        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        var products = db.Products.Where(p => p.TenantId == 1);

        var total = await products.SumAsync(p => p.Price * 2, TestContext.Current.CancellationToken);
        var latest = await products.MaxAsync(p => p.CreatedAt, TestContext.Current.CancellationToken);
        var average = await products.AverageAsync(p => p.Rating, TestContext.Current.CancellationToken);
        var none = await products.Where(p => p.Id < 0).SumAsync(p => p.Price, TestContext.Current.CancellationToken);

        Assert.Equal(171m, total);
        Assert.Equal(new DateTime(2026, 5, 1, 16, 45, 0, DateTimeKind.Utc), latest.ToUniversalTime());
        Assert.Equal(4.26, Math.Round(average, 2));
        Assert.Equal(0m, none);
    }

    public sealed class SpikeKey<T0, T1>
    {
        public T0 K0 { get; init; } = default!;

        public T1 K1 { get; init; } = default!;
    }
}

public sealed class SqliteGroupingSpikeTests() : GroupingSpikeTests(new SqliteDatabase());

public sealed class PostgresGroupingSpikeTests(PostgresContainer postgres) : GroupingSpikeTests(new PostgresDatabase(postgres));

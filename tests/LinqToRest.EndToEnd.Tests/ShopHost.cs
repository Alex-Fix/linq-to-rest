using LinqToRest.Client;
using LinqToRest.Modeling;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.IntegrationTests.Shop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.EndToEnd.Tests;

/// <summary>The client's view of the shop API.</summary>
public sealed class ShopClient(RestDataContextOptions<ShopClient> options) : RestDataContext(options)
{
    public DtoSet<ProductDto> Products => Set<ProductDto>();
    public DtoSet<CategoryDto> Categories => Set<CategoryDto>();
}

/// <summary>A seeded shop server and a factory of client contexts talking to it over the test server.</summary>
public sealed class ShopHost : IAsyncDisposable
{
    private static readonly DtoModel Model = ShopModel.Create();
    private readonly WebApplication _app;
    private readonly IAsyncDisposable? _database;

    private ShopHost(WebApplication app, IAsyncDisposable? database)
    {
        _app = app;
        _database = database;
    }

    public static async Task<ShopHost> StartAsync(ShopDatabase database)
    {
        var lease = await database.CreateAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<ShopDbContext>(lease.Configure);
        builder.Services.AddLinqToRest(options => options
            .UseDbContext<ShopDbContext>()
            .AddSetsFromAssembly(typeof(ShopHost).Assembly)
            .AddPolicy<ProductPolicy>()
            .AddPolicy<CategoryPolicy>());
        var app = builder.Build();
        app.MapLinqToRest("/api");
        await app.StartAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            await db.Database.EnsureCreatedAsync();
            ShopData.Seed(db);
        }

        return new ShopHost(app, lease.Owned);
    }

    /// <summary>A new unit of work for a tenant.</summary>
    public ShopClient Client(int tenant = 1, bool readOnly = false)
    {
        var http = _app.GetTestClient();
        http.BaseAddress = new Uri(http.BaseAddress!, "/api/");
        http.DefaultRequestHeaders.Add("X-Tenant", tenant.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (readOnly)
        {
            http.DefaultRequestHeaders.Add("X-Read-Only", "1");
        }

        return new ShopClient(new RestDataContextOptions<ShopClient>(Model, http));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RestToLinq.Client;
using RestToLinq.Modeling;
using RestToLinq.Server.Hosting;
using RestToLinq.Server.IntegrationTests.Shop;

namespace RestToLinq.EndToEnd.Tests;

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
    private readonly SqliteConnection _connection;

    private ShopHost(WebApplication app, SqliteConnection connection)
    {
        _app = app;
        _connection = connection;
    }

    public static async Task<ShopHost> StartAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<ShopDbContext>(options => options.UseSqlite(connection));
        builder.Services.AddRestToLinq(Model, options => options
            .UseDbContext<ShopDbContext>()
            .AddMappingsFromAssembly(typeof(ShopHost).Assembly)
            .AddPolicy<ProductPolicy>()
            .AddPolicy<CategoryPolicy>());
        var app = builder.Build();
        app.MapRestToLinq("/api");
        await app.StartAsync();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            await db.Database.EnsureCreatedAsync();
            ShopData.Seed(db);
        }

        return new ShopHost(app, connection);
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
        await _connection.DisposeAsync();
    }
}

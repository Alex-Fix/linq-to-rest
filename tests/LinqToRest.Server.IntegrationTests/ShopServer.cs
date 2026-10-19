using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.IntegrationTests.Shop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>A LinqToRest server over a seeded in-memory SQLite shop, reached through the ASP.NET Core test server.</summary>
public sealed class ShopServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly IAsyncDisposable? _database;

    private ShopServer(WebApplication app, IAsyncDisposable? database, ConcurrentQueue<string> sql)
    {
        Sql = sql;
        _app = app;
        _database = database;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    /// <summary>Gets every SQL command executed, to prove expressions run in the database.</summary>
    public ConcurrentQueue<string> Sql { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<ShopServer> StartAsync(
        ShopDatabase database,
        Action<LinqToRestServerBuilder>? configure = null,
        Action<WebApplication>? pipeline = null,
        IInterceptor? interceptor = null)
    {
        var lease = await database.CreateAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var sql = new ConcurrentQueue<string>();
        builder.Services.AddDbContext<ShopDbContext>(options =>
        {
            lease.Configure(options);
            options.LogTo(sql.Enqueue, [RelationalEventId.CommandExecuted]);
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });
        builder.Services.AddLinqToRest(ShopModel.Create(), options =>
        {
            options.UseDbContext<ShopDbContext>().AddMappingsFromAssembly(typeof(ShopServer).Assembly);
            configure?.Invoke(options);
        });

        var app = builder.Build();
        pipeline?.Invoke(app);
        app.MapLinqToRest("/api");
        await app.StartAsync();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            await db.Database.EnsureCreatedAsync();
            ShopData.Seed(db);
        }

        return new ShopServer(app, lease.Owned, sql);
    }

    public Task<HttpResponseMessage> QueryAsync(string set, string json, string contentType = "application/json") =>
        Client.SendAsync(new HttpRequestMessage(HttpMethod.Query, $"/api/{set}") { Content = new StringContent(json, Encoding.UTF8, contentType) }, TestContext.Current.CancellationToken);

    /// <summary>Sends a query and returns the parsed 200 response.</summary>
    public async Task<JsonElement> QueryOkAsync(string set, string json)
    {
        using var response = await QueryAsync(set, json);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    /// <summary>Sends a query and returns the problem response, asserting its status.</summary>
    public async Task<JsonElement> QueryProblemAsync(string set, string json, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var response = await QueryAsync(set, json);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(body).RootElement;
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

/// <summary>One read-only shop server shared by a test class, on one provider.</summary>
public abstract class ShopFixture(ShopDatabase database) : IAsyncLifetime
{
    private ShopServer? _server;

    public ShopDatabase Database { get; } = database;

    /// <summary>Gets the server, skipping the calling test when the provider isn't available.</summary>
    public ShopServer Server
    {
        get
        {
            Database.SkipIfUnavailable();
            return _server!;
        }
    }

    // A skip thrown while a fixture initializes would count as a failure, so tests skip lazily through Server.
    public async ValueTask InitializeAsync() => _server = Database.SkipReason is null ? await ShopServer.StartAsync(Database) : null;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

public sealed class SqliteShopFixture() : ShopFixture(new SqliteDatabase());

public sealed class PostgresShopFixture(PostgresContainer postgres) : ShopFixture(new PostgresDatabase(postgres));

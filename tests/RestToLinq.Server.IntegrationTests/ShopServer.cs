using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using RestToLinq.Server.Hosting;
using RestToLinq.Server.IntegrationTests.Shop;

namespace RestToLinq.Server.IntegrationTests;

/// <summary>A RestToLinq server over a seeded in-memory SQLite shop, reached through the ASP.NET Core test server.</summary>
public sealed class ShopServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly SqliteConnection _connection;

    private ShopServer(WebApplication app, SqliteConnection connection, ConcurrentQueue<string> sql)
    {
        Sql = sql;
        _app = app;
        _connection = connection;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    /// <summary>Gets every SQL command executed, to prove expressions run in the database.</summary>
    public ConcurrentQueue<string> Sql { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<ShopServer> StartAsync(Action<RestToLinqServerBuilder>? configure = null, Action<WebApplication>? pipeline = null)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var sql = new ConcurrentQueue<string>();
        builder.Services.AddDbContext<ShopDbContext>(options => options
            .UseSqlite(connection)
            .LogTo(sql.Enqueue, [RelationalEventId.CommandExecuted]));
        builder.Services.AddRestToLinq(ShopModel.Create(), options =>
        {
            options.UseDbContext<ShopDbContext>().AddMappingsFromAssembly(typeof(ShopServer).Assembly);
            configure?.Invoke(options);
        });

        var app = builder.Build();
        pipeline?.Invoke(app);
        app.MapRestToLinq("/api");
        await app.StartAsync();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            await db.Database.EnsureCreatedAsync();
            ShopData.Seed(db);
        }

        return new ShopServer(app, connection, sql);
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
        await _connection.DisposeAsync();
    }
}

/// <summary>One read-only shop server shared by a test class.</summary>
public sealed class ShopFixture : IAsyncLifetime
{
    public ShopServer Server { get; private set; } = null!;

    public async ValueTask InitializeAsync() => Server = await ShopServer.StartAsync();

    public async ValueTask DisposeAsync() => await Server.DisposeAsync();
}

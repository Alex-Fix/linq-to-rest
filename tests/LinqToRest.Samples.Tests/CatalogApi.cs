using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace LinqToRest.Samples.Tests;

/// <summary>Catalog.Api's own Program on a throwaway SQLite file, seeded the way the sample seeds itself.</summary>
public sealed class CatalogApi : WebApplicationFactory<Program>
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}.db");
    private readonly string _environment;

    public CatalogApi()
        : this(Environments.Development)
    {
    }

    private CatalogApi(string environment) => _environment = environment;

    /// <summary>Gets the host, for exporting the model.</summary>
    public IHost? Host { get; private set; }

    public static CatalogApi InEnvironment(string environment) => new(environment);

    public async Task<HttpClient> ClientAsync(string? role = null)
    {
        var client = CreateClient();
        if (role is not null)
        {
            using var login = await client.PostAsJsonAsync("/auth/dev-login", new { role }, TestContext.Current.CancellationToken);
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("token").GetString();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:CatalogSqlite", $"Data Source={_database}");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        Host = base.CreateHost(builder);
        return Host;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _database, _database + "-shm", _database + "-wal" }.Where(File.Exists))
        {
            File.Delete(file);
        }
    }
}

internal static class Requests
{
    public static async Task<(HttpStatusCode Status, JsonElement Body)> QueryAsync(this HttpClient client, string set, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Query, $"/api/{set}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));
    }

    public static async Task<JsonElement> QueryOkAsync(this HttpClient client, string set, string json)
    {
        var (status, body) = await client.QueryAsync(set, json);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
        return body;
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> BatchAsync(this HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/api/$batch", UriKind.Relative), content, TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));
    }

    public static long Total(this JsonElement result) => result.GetProperty("totalCount").GetInt64();

    public static string? Code(this JsonElement problem) => problem.GetProperty("code").GetString();
}

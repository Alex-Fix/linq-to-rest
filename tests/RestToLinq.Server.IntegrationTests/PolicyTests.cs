using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RestToLinq.Server.IntegrationTests.Shop;

namespace RestToLinq.Server.IntegrationTests;

public abstract class PolicyTests(ShopDatabase database) : IAsyncLifetime
{
    private ShopServer? _started;

    public async ValueTask InitializeAsync()
    {
        if (database.SkipReason is not null)
        {
            return;
        }

        _started = await ShopServer.StartAsync(database, options => options.AddPolicy<ProductPolicy>().AddPolicy<CategoryPolicy>());

        // Cross-tenant links: tenant 1's Notebook sits in tenant 2's Tools; tenant 2's Puzzle sits in tenant 1's Games.
        using var scope = _started.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        (await db.Products.FindAsync([5], TestContext.Current.CancellationToken))!.CategoryId = 3;
        (await db.Products.FindAsync([6], TestContext.Current.CancellationToken))!.TenantId = 2;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_started is not null)
        {
            await _started.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Gets the server, skipping the calling test when the provider isn't available.</summary>
    private ShopServer Server
    {
        get
        {
            database.SkipIfUnavailable();
            return _started!;
        }
    }

    [Fact]
    public async Task Rows_outside_the_filter_are_invisible_and_uncounted()
    {
        var result = await QueryAsync("products", """{"v":1,"count":true}""", tenant: 1);

        Assert.Equal([1, 2, 3, 5], JsonAssert.Ids(result));
        Assert.Equal(4, result.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task Cursor_pages_stay_inside_the_filter()
    {
        var first = await QueryAsync("products", """{"v":1,"orderBy":[{"member":"price"}],"page":{"mode":"cursor","take":3}}""", tenant: 1);
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await QueryAsync("products", """{"v":1,"orderBy":[{"member":"price"}],"page":{"mode":"cursor","take":3,"after":""" + "\"" + cursor + "\"}}", tenant: 1);

        Assert.Equal([5, 2, 1], JsonAssert.Ids(first));
        Assert.Equal([3], JsonAssert.Ids(second));
    }

    [Fact]
    public async Task Hidden_reference_navigation_behaves_as_missing()
    {
        var included = await QueryAsync("products", """{"v":1,"filter":{"eq":[{"member":"id"},5]},"include":["category"]}""", tenant: 1);
        var selected = await QueryAsync("products", """{"v":1,"filter":{"eq":[{"member":"id"},5]},"select":{"new":{"name":{"member":"category/name"},"category":{"member":"category"}}}}""", tenant: 1);
        var filtered = await QueryAsync("products", """{"v":1,"filter":{"eq":[{"member":"category/name"},"Tools"]}}""", tenant: 1);

        Assert.Equal(JsonValueKind.Null, JsonAssert.Single(included).GetProperty("category").ValueKind);
        JsonAssert.Equal("""{"name":null,"category":null}""", JsonAssert.Single(selected));
        Assert.Empty(JsonAssert.Ids(filtered));
    }

    [Fact]
    public async Task Hidden_collection_elements_are_left_out()
    {
        var included = await QueryAsync("categories", """{"v":1,"filter":{"eq":[{"member":"id"},2]},"include":["products"]}""", tenant: 1);
        var mapped = await QueryAsync("categories", """{"v":1,"filter":{"eq":[{"member":"id"},2]},"select":{"map":{"source":{"member":"products"},"var":"p","body":{"member":"p/name"}}}}""", tenant: 1);
        var counted = await QueryAsync("categories", """{"v":1,"filter":{"eq":[{"count":{"source":{"member":"products"}}},2]}}""", tenant: 1);

        Assert.Equal(["Chess", "Go"], JsonAssert.Single(included).GetProperty("products").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        JsonAssert.Equal("""["Chess","Go"]""", JsonAssert.Single(mapped));
        Assert.Equal([2], JsonAssert.Ids(counted));
    }

    [Fact]
    public async Task Navigation_filters_apply_through_the_hidden_parent()
    {
        // Tenant 2 sees Puzzle, whose category Games belongs to tenant 1: Games and its products stay hidden.
        var result = await QueryAsync("products", """{"v":1,"select":{"new":{"id":{"member":"id"},"siblings":{"count":{"source":{"member":"category/products"}}}}}}""", tenant: 2);

        JsonAssert.Equal("""[{"id":4,"siblings":1},{"id":6,"siblings":0}]""", result.GetProperty("items"));
    }

    [Fact]
    public async Task Denied_read_is_forbidden()
    {
        using var request = Request("products", """{"v":1}""", tenant: 1);
        request.Headers.Add("X-No-Read", "1");

        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("\"forbidden\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private async Task<JsonElement> QueryAsync(string set, string json, int tenant)
    {
        using var request = Request(set, json, tenant);
        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement;
    }

    private static HttpRequestMessage Request(string set, string json, int tenant)
    {
        var request = new HttpRequestMessage(HttpMethod.Query, $"/api/{set}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Tenant", tenant.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return request;
    }
}

public sealed class SqlitePolicyTests() : PolicyTests(new SqliteDatabase());

public sealed class PostgresPolicyTests(PostgresContainer postgres) : PolicyTests(new PostgresDatabase(postgres));

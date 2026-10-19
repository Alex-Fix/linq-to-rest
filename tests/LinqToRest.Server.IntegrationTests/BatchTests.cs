using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Server.IntegrationTests.Shop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

public abstract class BatchTests(ShopDatabase database) : IAsyncLifetime
{
    private const string ChessToken = "00000001-0000-0000-0000-000000000000";
    private ShopServer? _started;

    public async ValueTask InitializeAsync() =>
        _started = database.SkipReason is null ? await ShopServer.StartAsync(database, options => options.AddPolicy<ProductPolicy>().AddPolicy<CategoryPolicy>().Limits(limits => limits.MaxBatchOperations = 5)) : null;

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
    public async Task Inserts_reference_each_other_and_come_back_through_the_projection()
    {
        var response = await BatchOkAsync("""
            {"v":1,"operations":[
              {"id":"c","op":"insert","set":"categories","values":{"name":"Puzzles"}},
              {"id":"p","op":"insert","set":"products","values":{"name":"Cube","price":9.5,"status":"Active","isFeatured":true},"refs":{"categoryId":"c"}}]}
            """);

        var results = response.GetProperty("results");
        var category = results[0].GetProperty("value");
        var product = results[1].GetProperty("value");
        Assert.Equal("Puzzles", category.GetProperty("name").GetString());
        Assert.Equal(category.GetProperty("id").GetInt32(), product.GetProperty("categoryId").GetInt32());
        Assert.Equal("Puzzles", product.GetProperty("categoryName").GetString());
        Assert.Equal("2026-10-18T12:00:00Z", product.GetProperty("createdAt").GetString());
        Assert.True(product.GetProperty("isFeatured").GetBoolean());
        Assert.NotEqual(Guid.Empty, product.GetProperty("version").GetGuid());

        var stored = await FindAsync(product.GetProperty("id").GetInt32());
        Assert.Equal((1, 9.5m), (stored!.TenantId, stored.Price));
    }

    [Fact]
    public async Task Update_writes_only_sent_members_and_returns_the_new_token()
    {
        var response = await BatchOkAsync($$"""
            {"v":1,"operations":[{"id":"u","op":"update","set":"products","key":{"id":1},"values":{"price":21},"token":"{{ChessToken}}"}]}
            """);

        var value = response.GetProperty("results")[0].GetProperty("value");
        Assert.Equal(21, value.GetProperty("price").GetDecimal());
        Assert.Equal("Chess", value.GetProperty("name").GetString());
        Assert.NotEqual(ChessToken, value.GetProperty("version").GetString());
        Assert.Equal("Wooden board", value.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Update_hook_runs_after_client_values()
    {
        var response = await BatchOkAsync("""
            {"v":1,"operations":[{"id":"u","op":"update","set":"products","key":{"id":2},"values":{"name":"Dune II"},"token":"00000002-0000-0000-0000-000000000000"}]}
            """);

        Assert.Equal("updated for tenant 1", response.GetProperty("results")[0].GetProperty("value").GetProperty("description").GetString());
    }

    [Fact]
    public async Task Delete_removes_the_row()
    {
        var response = await BatchOkAsync($$"""
            {"v":1,"operations":[{"id":"d","op":"delete","set":"products","key":{"id":1},"token":"{{ChessToken}}"}]}
            """);

        Assert.False(response.GetProperty("results")[0].TryGetProperty("value", out _));
        Assert.Null(await FindAsync(1));
    }

    [Fact]
    public async Task Stale_token_conflicts_and_rolls_back_the_whole_batch()
    {
        var problem = await BatchProblemAsync("""
            {"v":1,"operations":[
              {"id":"c","op":"insert","set":"categories","values":{"name":"Never"}},
              {"id":"u","op":"update","set":"products","key":{"id":1},"values":{"price":1},"token":"00000009-0000-0000-0000-000000000000"}]}
            """, HttpStatusCode.PreconditionFailed);

        JsonAssert.Problem(problem, "concurrency_conflict", "");
        Assert.Equal("u", problem.GetProperty("operationId").GetString());
        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        Assert.False(await db.Categories.AnyAsync(c => c.Name == "Never", TestContext.Current.CancellationToken));
        Assert.Equal(20m, (await FindAsync(1))!.Price);
    }

    public static readonly TheoryData<string, HttpStatusCode, string, string> Rejected = new()
    {
        { """{"id":"x","op":"update","set":"products","key":{"id":1},"values":{"id":7},"token":"00000001-0000-0000-0000-000000000000"}""", HttpStatusCode.BadRequest, "not_writable", "/operations/0/values" },
        { """{"id":"x","op":"update","set":"products","key":{"id":1},"values":{"categoryName":"x"},"token":"00000001-0000-0000-0000-000000000000"}""", HttpStatusCode.BadRequest, "not_writable", "/operations/0/values" },
        { """{"id":"x","op":"update","set":"products","key":{"id":1},"values":{"version":"00000001-0000-0000-0000-000000000000"},"token":"00000001-0000-0000-0000-000000000000"}""", HttpStatusCode.BadRequest, "not_writable", "/operations/0/values" },
        { """{"id":"x","op":"insert","set":"products","values":{"tenantId":2}}""", HttpStatusCode.BadRequest, "unknown_member", "/operations/0/values" },
        { """{"id":"x","op":"insert","set":"products","values":{"price":"free"}}""", HttpStatusCode.BadRequest, "type_mismatch", "/operations/0/values" },
        { """{"id":"x","op":"insert","set":"products","values":{"name":null}}""", HttpStatusCode.BadRequest, "type_mismatch", "/operations/0/values" },
        { """{"id":"x","op":"insert","set":"products","key":{"id":1}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0" },
        { """{"id":"x","op":"update","set":"products","key":{"id":1},"values":{"price":1}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/token" },
        { """{"id":"x","op":"update","set":"products","values":{"price":1},"token":"00000001-0000-0000-0000-000000000000"}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/key" },
        { """{"id":"x","op":"update","set":"products","key":{"sku":1},"values":{"price":1},"token":"00000001-0000-0000-0000-000000000000"}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/key" },
        { """{"id":"x","op":"delete","set":"products","key":{"id":1},"values":{"price":1},"token":"00000001-0000-0000-0000-000000000000"}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0" },
        { """{"id":"x","op":"insert","set":"products","refs":{"categoryId":"nope"}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/refs" },
        { """{"id":"x","op":"insert","set":"products","refs":{"name":"x"}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/refs" },
        { """{"id":"x","op":"insert","set":"nope"}""", HttpStatusCode.NotFound, "not_found", "/operations/0/set" },
        { """{"id":"x","op":"update","set":"products","key":{"id":4},"values":{"price":1},"token":"00000004-0000-0000-0000-000000000000"}""", HttpStatusCode.NotFound, "not_found", "/operations/0" },
        { """{"id":"x","op":"update","set":"products","key":{"id":99},"values":{"price":1},"token":"00000004-0000-0000-0000-000000000000"}""", HttpStatusCode.NotFound, "not_found", "/operations/0" },
        { """{"id":"x","op":"insert","set":"products","values":{"name":"Saw","categoryId":3}}""", HttpStatusCode.NotFound, "not_found", "/operations/0/values" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task Invalid_operation_is_rejected_with_its_id(string operation, HttpStatusCode status, string code, string location)
    {
        var problem = await BatchProblemAsync($$"""{"v":1,"operations":[{{operation}}]}""", status);

        JsonAssert.Problem(problem, code, location);
        Assert.Equal("x", problem.GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task Ref_must_point_at_an_earlier_insert_of_the_navigation_target()
    {
        var later = await BatchProblemAsync("""
            {"v":1,"operations":[
              {"id":"p","op":"insert","set":"products","values":{"name":"Cube"},"refs":{"categoryId":"c"}},
              {"id":"c","op":"insert","set":"categories","values":{"name":"Puzzles"}}]}
            """);
        var wrongSet = await BatchProblemAsync("""
            {"v":1,"operations":[
              {"id":"o","op":"insert","set":"products","values":{"name":"Other"}},
              {"id":"p","op":"insert","set":"products","values":{"name":"Cube"},"refs":{"categoryId":"o"}}]}
            """);

        JsonAssert.Problem(later, "invalid_batch", "/operations/0/refs");
        JsonAssert.Problem(wrongSet, "invalid_batch", "/operations/1/refs");
    }

    [Fact]
    public async Task Denied_write_is_forbidden()
    {
        using var request = Request("""{"v":1,"operations":[{"id":"x","op":"insert","set":"products","values":{"name":"Saw"}}]}""");
        request.Headers.Add("X-Read-Only", "1");

        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Operation_limit_is_enforced()
    {
        var operations = string.Join(",", Enumerable.Range(0, 6).Select(i => "{\"id\":\"o" + i + "\",\"op\":\"insert\",\"set\":\"categories\",\"values\":{\"name\":\"C" + i + "\"}}"));

        var problem = await BatchProblemAsync($$"""{"v":1,"operations":[{{operations}}]}""");

        JsonAssert.Problem(problem, "limit_exceeded", "/operations");
    }

    private async Task<Product?> FindAsync(int id)
    {
        using var scope = Server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Products.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task<JsonElement> BatchOkAsync(string json)
    {
        using var request = Request(json);
        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<JsonElement> BatchProblemAsync(string json, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        using var request = Request(json);
        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static HttpRequestMessage Request(string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/$batch") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Tenant", "1");
        return request;
    }
}

public sealed class SqliteBatchTests() : BatchTests(new SqliteDatabase());

public sealed class PostgresBatchTests(PostgresContainer postgres) : BatchTests(new PostgresDatabase(postgres));

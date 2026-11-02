using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Server.IntegrationTests.Shop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>
/// Link and unlink: one-to-many (products' lines, required; categories' products, optional) and many-to-many
/// (products' labels), through both sets' policies, in one transaction with the rest of the batch.
/// </summary>
public abstract class LinkTests(ShopDatabase database) : IAsyncLifetime
{
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

    private ShopServer Server
    {
        get
        {
            database.SkipIfUnavailable();
            return _started!;
        }
    }

    [Fact]
    public async Task Linking_a_line_moves_it_to_the_parent()
    {
        var response = await BatchOkAsync("""{"id":"x","op":"link","set":"products","key":{"id":1},"navigation":"lines","item":{"key":{"id":21}}}""");

        Assert.False(response.GetProperty("results")[0].TryGetProperty("value", out _));
        Assert.Equal(1, (await LineAsync(21))!.ProductId);
    }

    [Fact]
    public async Task Unlinking_a_required_line_deletes_it()
    {
        await BatchOkAsync("""{"id":"x","op":"unlink","set":"products","key":{"id":1},"navigation":"lines","item":{"key":{"id":12}}}""");

        Assert.Null(await LineAsync(12));
        Assert.NotNull(await LineAsync(11));
    }

    [Fact]
    public async Task Unlinking_an_optional_member_detaches_it_and_runs_the_update_hook()
    {
        await BatchOkAsync("""{"id":"x","op":"unlink","set":"categories","key":{"id":1},"navigation":"products","item":{"key":{"id":2},"token":"00000002-0000-0000-0000-000000000000"}}""");

        var dune = (await ProductAsync(2))!;
        Assert.Null(dune.CategoryId);
        Assert.Equal("updated for tenant 1", dune.Description);
        Assert.NotEqual(new Guid(2, 0, 0, new byte[8]), dune.Version);
    }

    [Fact]
    public async Task Many_to_many_links_add_and_remove_join_rows_and_repeat_as_a_no_op()
    {
        await BatchOkAsync(
            """{"id":"a","op":"link","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":3}}}""",
            """{"id":"b","op":"link","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":1}}}""",
            """{"id":"c","op":"unlink","set":"products","key":{"id":3},"navigation":"labels","item":{"key":{"id":1}}}""");

        Assert.Equal(["Eco", "Gift"], await LabelsAsync(1));
        Assert.Equal(["New"], await LabelsAsync(3));
    }

    [Fact]
    public async Task Parents_and_items_inserted_earlier_are_linked_by_ref()
    {
        var response = await BatchOkAsync(
            """{"id":"c","op":"insert","set":"categories","values":{"name":"Puzzles"}}""",
            """{"id":"m","op":"link","set":"categories","ref":"c","navigation":"products","item":{"key":{"id":6},"token":"00000006-0000-0000-0000-000000000000"}}""",
            """{"id":"l","op":"insert","set":"labels","values":{"name":"Sale"}}""",
            """{"id":"n","op":"link","set":"products","key":{"id":2},"navigation":"labels","item":{"ref":"l"}}""",
            """{"id":"o","op":"link","set":"products","key":{"id":5},"navigation":"labels","item":{"ref":"l"}}""");

        var category = response.GetProperty("results")[0].GetProperty("value").GetProperty("id").GetInt32();
        Assert.Equal(category, (await ProductAsync(6))!.CategoryId);
        Assert.Equal(["Sale"], await LabelsAsync(2));
        Assert.Equal(["Sale"], await LabelsAsync(5));
    }

    [Fact]
    public async Task A_link_and_unlink_of_new_rows_cancel_out()
    {
        await BatchOkAsync(
            """{"id":"l","op":"insert","set":"labels","values":{"name":"Brief"}}""",
            """{"id":"a","op":"link","set":"products","key":{"id":2},"navigation":"labels","item":{"ref":"l"}}""",
            """{"id":"b","op":"unlink","set":"products","key":{"id":2},"navigation":"labels","item":{"ref":"l"}}""");

        Assert.Empty(await LabelsAsync(2));
    }

    public static readonly TheoryData<string, HttpStatusCode, string, string> Rejected = new()
    {
        { """{"id":"x","op":"link","set":"products","key":{"id":4},"navigation":"labels","item":{"key":{"id":1}}}""", HttpStatusCode.NotFound, "not_found", "/operations/0/key" },
        { """{"id":"x","op":"link","set":"categories","key":{"id":1},"navigation":"products","item":{"key":{"id":4},"token":"00000004-0000-0000-0000-000000000000"}}""", HttpStatusCode.NotFound, "not_found", "/operations/0/item/key" },
        { """{"id":"x","op":"link","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":99}}}""", HttpStatusCode.NotFound, "not_found", "/operations/0/item/key" },
        { """{"id":"x","op":"unlink","set":"products","key":{"id":1},"navigation":"lines","item":{"key":{"id":21}}}""", HttpStatusCode.NotFound, "not_found", "/operations/0/item" },
        { """{"id":"x","op":"unlink","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":2}}}""", HttpStatusCode.NotFound, "not_found", "/operations/0/item" },
        { """{"id":"x","op":"link","set":"categories","key":{"id":1},"navigation":"products","item":{"key":{"id":5}}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/item/token" },
        { """{"id":"x","op":"link","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":3},"token":"00000001-0000-0000-0000-000000000000"}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/item/token" },
        { """{"id":"x","op":"link","set":"categories","key":{"id":1},"navigation":"products","item":{"key":{"id":5},"token":"00000009-0000-0000-0000-000000000000"}}""", HttpStatusCode.PreconditionFailed, "concurrency_conflict", "" },
        { """{"id":"x","op":"link","set":"products","key":{"id":1},"navigation":"category","item":{"key":{"id":1}}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/navigation" },
        { """{"id":"x","op":"link","set":"products","key":{"id":1},"navigation":"nope","item":{"key":{"id":1}}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/navigation" },
        { """{"id":"x","op":"link","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"name":"Gift"}}}""", HttpStatusCode.BadRequest, "invalid_batch", "/operations/0/item/key" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task Invalid_link_is_rejected_with_its_id(string operation, HttpStatusCode status, string code, string location)
    {
        var problem = await BatchProblemAsync(status, null, operation);

        JsonAssert.Problem(problem, code, location);
        Assert.Equal("x", problem.GetProperty("operationId").GetString());
    }

    [Fact]
    public async Task Ref_must_name_an_earlier_insert_of_the_right_set()
    {
        var problem = await BatchProblemAsync(
            HttpStatusCode.BadRequest,
            null,
            """{"id":"l","op":"insert","set":"labels","values":{"name":"Odd"}}""",
            """{"id":"x","op":"link","set":"categories","key":{"id":1},"navigation":"products","item":{"ref":"l"}}""");

        JsonAssert.Problem(problem, "invalid_batch", "/operations/1/item/ref");
    }

    [Fact]
    public async Task Policies_of_both_sets_apply()
    {
        var problem = await BatchProblemAsync(
            HttpStatusCode.Forbidden,
            "X-Read-Only",
            """{"id":"x","op":"link","set":"categories","key":{"id":1},"navigation":"products","item":{"key":{"id":5},"token":"00000005-0000-0000-0000-000000000000"}}""");

        JsonAssert.Problem(problem, "forbidden", "/operations/0");
        Assert.Null((await ProductAsync(5))!.CategoryId);
    }

    [Fact]
    public async Task A_failing_link_rolls_back_the_whole_batch()
    {
        await BatchProblemAsync(
            HttpStatusCode.NotFound,
            null,
            """{"id":"a","op":"link","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":3}}}""",
            """{"id":"x","op":"unlink","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":2}}}""");

        Assert.Equal(["Gift"], await LabelsAsync(1));
    }

    [Fact]
    public async Task Collections_without_links_are_read_only()
    {
        await using var server = await ShopServer.StartAsync(database, sets: options => options
            .AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.All).HasMany(p => p.Labels))
            .AddSet<Label>(b => b.ToSet("labels")));
        using var request = Request(null, """{"id":"x","op":"link","set":"products","key":{"id":1},"navigation":"labels","item":{"key":{"id":3}}}""");
        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        JsonAssert.Problem(JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement, "operation_not_allowed", "/operations/0/navigation");
    }

    private async Task<List<string>> LabelsAsync(int productId)
    {
        using var scope = Server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
        return await db.Products.Where(p => p.Id == productId).SelectMany(p => p.Labels).OrderBy(l => l.Name).Select(l => l.Name).ToListAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Product?> ProductAsync(int id)
    {
        using var scope = Server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task<OrderLine?> LineAsync(int id)
    {
        using var scope = Server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ShopDbContext>().Set<OrderLine>().AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task<JsonElement> BatchOkAsync(params string[] operations)
    {
        using var request = Request(null, operations);
        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private async Task<JsonElement> BatchProblemAsync(HttpStatusCode status, string? header, params string[] operations)
    {
        using var request = Request(header, operations);
        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static HttpRequestMessage Request(string? header, params string[] operations)
    {
        var json = $$"""{"v":1,"operations":[{{string.Join(",", operations)}}]}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/$batch") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Tenant", "1");
        if (header is not null)
        {
            request.Headers.Add(header, "1");
        }

        return request;
    }
}

public sealed class SqliteLinkTests() : LinkTests(new SqliteDatabase());

public sealed class PostgresLinkTests(PostgresContainer postgres) : LinkTests(new PostgresDatabase(postgres));

public sealed class SqlServerLinkTests(SqlServerContainer sqlServer) : LinkTests(new SqlServerDatabase(sqlServer));

using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Server.IntegrationTests.Shop;
using LinqToRest.Server.Sets;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>Grouped queries and whole-query aggregates: GROUP BY in SQL, policies first, per-column opt-in.</summary>
public abstract class GroupingTests(ShopDatabase database) : IAsyncLifetime
{
    // Written as $COUNT in the requests below.
    private const string Count = """{"count":{"source":{"group":{}},"var":"c","body":true}}""";
    private ShopServer? _started;

    public async ValueTask InitializeAsync() =>
        _started = database.SkipReason is null
            ? await ShopServer.StartAsync(
                database,
                options => options.AddPolicy<ProductPolicy>().AddPolicy<CategoryPolicy>().Limits(limits => limits.MaxGroups = 3),
                sets: options => options.AddSet<Product>(ConfigureProducts).AddSet<Category>(ConfigureCategories).AddSet(new OrderLineSet()).AddShopValueTypes())
            : null;

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
    public async Task Groups_by_a_reference_with_every_aggregate_in_sql()
    {
        Server.Sql.Clear();

        var body = await QueryAsync("""
            {"v":1,"groupBy":["category/name"],
             "select":{"new":{
               "category":{"key":0},
               "count":$COUNT,
               "featured":{"count":{"source":{"group":{}},"var":"f","where":{"member":"f/isFeatured"},"body":true}},
               "from":{"min":{"source":{"group":{}},"var":"m","body":{"member":"m/price"}}},
               "to":{"max":{"source":{"group":{}},"var":"m","body":{"member":"m/price"}}},
               "total":{"sum":{"source":{"group":{}},"var":"s","body":{"mul":[{"member":"s/price"},2]}}},
               "rating":{"avg":{"source":{"group":{}},"var":"a","body":{"member":"a/rating"}}},
               "latest":{"max":{"source":{"group":{}},"var":"d","body":{"member":"d/createdAt"}}}}},
             "orderBy":[{"member":"count","desc":true}],
             "page":{"mode":"offset","take":10},
             "count":true}
            """);

        var games = body.GetProperty("items")[0];
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            ("Games", 3, 2, 20m, 30m, 140m, 4.2, "2026-05-01T16:45:00Z"),
            (games.GetProperty("category").GetString(), games.GetProperty("count").GetInt32(), games.GetProperty("featured").GetInt32(),
             games.GetProperty("from").GetDecimal(), games.GetProperty("to").GetDecimal(), games.GetProperty("total").GetDecimal(),
             Math.Round(games.GetProperty("rating").GetDouble(), 2), games.GetProperty("latest").GetDateTime().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)));

        // Books and products without a category tie on count; the key breaks the tie, null smallest on every provider.
        Assert.Equal([null, "Books"], body.GetProperty("items").EnumerateArray().Skip(1).Select(item => item.GetProperty("category").GetString()));
        Assert.Contains(Server.Sql, sql => sql.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Having_and_order_use_the_select_fields()
    {
        var body = await QueryAsync("""
            {"v":1,"groupBy":["status"],
             "select":{"new":{"status":{"key":0},"count":$COUNT,"cheapest":{"min":{"source":{"group":{}},"var":"m","body":{"member":"m/price"}}}}},
             "having":{"or":[{"gt":[{"member":"count"},1]},{"lt":[{"member":"cheapest"},5]}]},
             "orderBy":[{"member":"cheapest"}]}
            """);

        Assert.Equal(
            [("Retired", 1), ("Active", 3)],
            body.GetProperty("items").EnumerateArray().Select(item => (item.GetProperty("status").GetString(), item.GetProperty("count").GetInt32())));
    }

    [Fact]
    public async Task Several_keys_computed_keys_and_dates()
    {
        var body = await QueryAsync("""
            {"v":1,"groupBy":["expensive","isFeatured","releasedOn"],
             "select":{"new":{"expensive":{"key":0},"featured":{"key":1},"released":{"key":2},"count":$COUNT}},
             "having":{"eq":[{"member":"expensive"},true]}}
            """);

        Assert.Equal(
            [(true, false, "2026-03-01", 1), (true, true, "2025-12-01", 1), (true, true, "2026-04-15", 1)],
            body.GetProperty("items").EnumerateArray()
                .Select(item => (item.GetProperty("expensive").GetBoolean(), item.GetProperty("featured").GetBoolean(), item.GetProperty("released").GetString(), item.GetProperty("count").GetInt32()))
                .Order());
    }

    [Fact]
    public async Task Groups_page_with_skip_and_take_and_count_with_policies_applied_first()
    {
        var first = await QueryAsync("""{"v":1,"groupBy":["categoryId"],"select":{"new":{"id":{"key":0},"count":$COUNT}},"orderBy":[{"member":"count","desc":true}],"page":{"mode":"offset","take":1},"count":true}""");
        var rest = await QueryAsync("""{"v":1,"groupBy":["categoryId"],"select":{"new":{"id":{"key":0},"count":$COUNT}},"orderBy":[{"member":"count","desc":true}],"page":{"mode":"offset","skip":1,"take":5}}""");
        var otherTenant = await QueryAsync("""{"v":1,"groupBy":["categoryId"],"select":{"new":{"id":{"key":0},"count":$COUNT}}}""", tenant: 2);

        Assert.Equal((3, 2), (first.GetProperty("totalCount").GetInt32(), first.GetProperty("items")[0].GetProperty("id").GetInt32()));
        Assert.Equal(2, rest.GetProperty("items").GetArrayLength());
        Assert.Equal([(3, 1)], otherTenant.GetProperty("items").EnumerateArray().Select(item => (item.GetProperty("id").GetInt32(), item.GetProperty("count").GetInt32())));
    }

    [Fact]
    public async Task Without_take_the_group_count_is_capped()
    {
        // MaxGroups is 3: three statuses fit, five names don't.
        var statuses = await QueryAsync("""{"v":1,"groupBy":["status"],"select":{"new":{"status":{"key":0},"count":$COUNT}}}""");
        var names = await ProblemAsync("""{"v":1,"groupBy":["expensiveName"],"select":{"new":{"name":{"key":0}}}}""");

        Assert.Equal(3, statuses.GetProperty("items").GetArrayLength());
        Assert.Equal(("limit_exceeded", "groups", "/groupBy"), (names.GetProperty("code").GetString(), names.GetProperty("limit").GetString(), names.GetProperty("pointer").GetString()));
    }

    [Fact]
    public async Task Whole_query_aggregates_have_no_keys()
    {
        var all = await QueryAsync("""
            {"v":1,"groupBy":[],
             "select":{"new":{"total":{"sum":{"source":{"group":{}},"var":"s","body":{"member":"s/price"}}},"latest":{"max":{"source":{"group":{}},"var":"d","body":{"member":"d/createdAt"}}}}}}
            """);
        var none = await QueryAsync("""
            {"v":1,"filter":{"lt":[{"member":"price"},0]},"groupBy":[],
             "select":{"sum":{"source":{"group":{}},"var":"s","body":{"member":"s/price"}}}}
            """);

        Assert.Equal(85.5m, all.GetProperty("items")[0].GetProperty("total").GetDecimal());
        Assert.Equal(0, none.GetProperty("items").GetArrayLength());
    }

    [Theory]
    [InlineData("""{"v":1,"groupBy":["name"],"select":{"key":0}}""", "not_groupable", "/groupBy/0")]
    [InlineData("""{"v":1,"groupBy":["lines"],"select":{"key":0}}""", "not_groupable", "/groupBy/0")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"sum":{"source":{"group":{}},"var":"s","body":{"member":"s/categoryId"}}}}""", "not_aggregatable", "/select/sum/body")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"new":{"name":{"member":"name"}}}}""", "invalid_query", "/select/new/name")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"key":1}}""", "invalid_query", "/select")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"any":{"source":{"group":{}},"var":"a","body":true}}}""", "invalid_query", "/select")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"sum":{"source":{"group":{}},"var":"s","body":{"count":{"source":{"member":"s/lines"}}}}}}""", "invalid_query", "/select/sum/body")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"key":0},"having":{"gt":[{"member":"count"},1]}}""", "unknown_member", "/having/gt/0")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"new":{"s":{"key":0}}},"orderBy":[{"member":"price"}]}""", "unknown_member", "/orderBy/0/member")]
    [InlineData("""{"v":1,"groupBy":["status"]}""", "invalid_query", "/select")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"key":0},"include":["category"]}""", "invalid_query", "/include")]
    [InlineData("""{"v":1,"groupBy":["status"],"select":{"key":0},"page":{"mode":"cursor"}}""", "invalid_query", "/page/mode")]
    [InlineData("""{"v":1,"having":{"eq":[1,1]}}""", "invalid_query", "/having")]
    [InlineData("""{"v":1,"select":{"new":{"k":{"key":0}}}}""", "invalid_query", "/select/new/k")]
    [InlineData("""{"v":1,"filter":{"gt":[{"count":{"source":{"group":{}}}},0]}}""", "invalid_query", "/filter/gt/0")]
    public async Task Rejects_what_grouping_does_not_allow(string json, string code, string location)
    {
        var problem = await ProblemAsync(json);

        Assert.Equal((code, location), (problem.GetProperty("code").GetString(), problem.GetProperty("pointer").GetString()));
    }

    // Lambdas, not configuration classes: the other tests' servers scan this assembly for those.
    private static void ConfigureProducts(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products");
        builder.Property(p => p.Name);
        builder.Property(p => p.Price).IsSortable().IsAggregatable();
        builder.Property(p => p.Status).IsGroupable();
        builder.Property(p => p.CreatedAt).IsAggregatable();
        builder.Property(p => p.ReleasedOn).IsGroupable();
        builder.Property(p => p.Rating).IsAggregatable();
        builder.Property(p => p.IsFeatured).IsGroupable();
        builder.Property(p => p.CategoryId).IsGroupable();
        builder.Computed("expensive", p => p.Price > 15).IsGroupable();
        builder.Computed("expensiveName", p => p.Name).IsRequired().IsGroupable();
        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasMany(p => p.Lines);
    }

    private static void ConfigureCategories(EntitySetBuilder<Category> builder)
    {
        builder.ToSet("categories");
        builder.Property(c => c.Name).IsGroupable();
        builder.HasMany(c => c.Products);
    }

    private async Task<JsonElement> QueryAsync(string json, int tenant = 1)
    {
        var (status, body) = await SendAsync(json, tenant);
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
        return body;
    }

    private async Task<JsonElement> ProblemAsync(string json)
    {
        var (status, body) = await SendAsync(json, 1);
        Assert.True(status == HttpStatusCode.BadRequest, $"{(int)status}: {body}");
        return body;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(string json, int tenant)
    {
        using var request = new HttpRequestMessage(HttpMethod.Query, "/api/products");
        request.Content = new StringContent(json.Replace("$COUNT", Count, StringComparison.Ordinal), Encoding.UTF8, "application/json");
        request.Headers.Add("X-Tenant", tenant.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var response = await Server.Client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone());
    }
}

public sealed class SqliteGroupingTests() : GroupingTests(new SqliteDatabase());

public sealed class PostgresGroupingTests(PostgresContainer postgres) : GroupingTests(new PostgresDatabase(postgres));

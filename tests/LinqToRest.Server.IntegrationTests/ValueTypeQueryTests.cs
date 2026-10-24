using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Server.IntegrationTests.Shop;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>Value paths in filters, sorts, cursors, groups, lambdas and selects, on every EF mapping, in SQL.</summary>
public abstract class ValueTypeQueryTests(ShopFixture shop)
{
    [Fact]
    public async Task Nested_complex_members_filter_and_sort()
    {
        var result = await shop.Server.QueryOkAsync("products", """
            {"v":1,"filter":{"and":[{"gt":[{"member":"size/weight/amount"},1]},{"eq":[{"member":"size/weight/unit"},"kg"]}]},
             "orderBy":[{"member":"size/width","desc":true}]}
            """);

        Assert.Equal([3, 1], JsonAssert.Ids(result));
    }

    [Theory]
    [InlineData("""{"eq":[{"member":"origin"},null]}""", new[] { 4, 5, 6 })]
    [InlineData("""{"eq":[null,{"member":"origin"}]}""", new[] { 4, 5, 6 })]
    [InlineData("""{"ne":[{"member":"origin"},null]}""", new[] { 1, 2, 3 })]
    [InlineData("""{"eq":[{"member":"origin/city"},null]}""", new[] { 1, 3, 4, 5, 6 })]
    [InlineData("""{"eq":[{"member":"category/contact"},null]}""", new[] { 2, 5 })]
    [InlineData("""{"endsWith":[{"member":"category/contact/email"},"@shop.example"]}""", new[] { 1, 3, 4, 6 })]
    [InlineData("""{"any":{"source":{"member":"tags"},"var":"t","body":{"eq":[{"member":"origin/country"},"IN"]}}}""", new[] { 1 })]
    public async Task Values_filter_by_presence_and_members_on_every_mapping(string filter, int[] ids)
    {
        var result = await shop.Server.QueryOkAsync("products", $$"""{"v":1,"filter":{{filter}},"orderBy":[{"member":"id"}]}""");

        Assert.Equal(ids, JsonAssert.Ids(result));
    }

    [Fact]
    public async Task Whole_values_select_as_objects_or_null_in_sql()
    {
        await using var server = await ShopServer.StartAsync(shop.Database);
        server.Sql.Clear();

        var result = await server.QueryOkAsync("products", """
            {"v":1,"filter":{"in":[{"member":"id"},[1,2,4]]},"orderBy":[{"member":"id"}],
             "select":{"new":{"origin":{"member":"origin"},"weight":{"member":"size/weight"},"city":{"member":"origin/city"},
                              "contact":{"member":"category/contact"},
                              "shipping":{"map":{"source":{"member":"lines"},"var":"l","body":{"member":"l/shipping"}}}}}}
            """);

        var items = result.GetProperty("items");
        JsonAssert.Equal("""
            {"origin":{"country":"IN","city":null},"weight":{"amount":1.2,"unit":"kg"},"city":null,
             "contact":{"email":"games@shop.example","phone":"+380 44 000"},"shipping":[{"country":"UA","city":"Lviv"},null]}
            """, items[0]);
        JsonAssert.Equal("""
            {"origin":{"country":"US","city":"Philadelphia"},"weight":{"amount":0.4,"unit":"kg"},"city":"Philadelphia",
             "contact":null,"shipping":[{"country":"PL","city":"Krakow"}]}
            """, items[1]);
        JsonAssert.Equal("""
            {"origin":null,"weight":{"amount":0.8,"unit":"kg"},"city":null,"contact":{"email":"tools@shop.example","phone":null},
             "shipping":[null]}
            """, items[2]);
        // One query; each optional value's presence (origin, contact, shipping) is a column test in SQL.
        var sql = Assert.Single(server.Sql);
        Assert.Equal(3, sql.Split("IS NOT NULL").Length - 1);
    }

    [Fact]
    public async Task Cursor_pages_sort_by_required_value_members()
    {
        var first = await shop.Server.QueryOkAsync("products", """{"v":1,"orderBy":[{"member":"size/weight/amount"}],"page":{"mode":"cursor","take":4}}""");
        var cursor = first.GetProperty("nextCursor").GetString();
        var second = await shop.Server.QueryOkAsync("products", $$$"""{"v":1,"orderBy":[{"member":"size/weight/amount"}],"page":{"mode":"cursor","take":4,"after":"{{{cursor}}}"}}""");

        Assert.Equal([5, 6, 2, 4], JsonAssert.Ids(first));
        Assert.Equal([1, 3], JsonAssert.Ids(second));
    }

    [Fact]
    public async Task Groups_by_value_members_and_aggregates_them()
    {
        var result = await shop.Server.QueryOkAsync("products", """
            {"v":1,"groupBy":["size/weight/unit"],
             "select":{"new":{"unit":{"key":0},"heaviest":{"max":{"source":{"group":{}},"var":"p","body":{"member":"p/size/weight/amount"}}}}}}
            """);

        JsonAssert.Equal("""{"unit":"kg","heaviest":2.5}""", JsonAssert.Single(result));
    }

    [Theory]
    [InlineData("""{"filter":{"eq":[{"member":"origin/secret"},1]}}""", "unknown_member", "/filter/eq/0")]
    [InlineData("""{"filter":{"eq":[{"member":"size/width/x"},1]}}""", "unknown_member", "/filter/eq/0")]
    [InlineData("""{"filter":{"eq":[{"member":"size"},null]}}""", "type_mismatch", "/filter")]
    [InlineData("""{"filter":{"eq":[{"member":"origin"},"IN"]}}""", "type_mismatch", "/filter")]
    [InlineData("""{"filter":{"eq":[{"member":"origin"},{"member":"origin"}]}}""", "type_mismatch", "/filter")]
    [InlineData("""{"filter":{"in":[{"member":"origin"},["IN"]]}}""", "type_mismatch", "/filter/in/0")]
    [InlineData("""{"filter":{"member":"origin"}}""", "type_mismatch", "/filter")]
    [InlineData("""{"orderBy":[{"member":"origin"}]}""", "not_sortable", "/orderBy/0/member")]
    [InlineData("""{"orderBy":[{"member":"origin/city"}]}""", "not_sortable", "/orderBy/0/member")]
    [InlineData("""{"orderBy":[{"member":"origin/country"}],"page":{"mode":"cursor"}}""", "cursor_nullable_sort", "/orderBy/0")]
    [InlineData("""{"groupBy":["origin"],"select":{"key":0}}""", "not_groupable", "/groupBy/0")]
    [InlineData("""{"filter":{"eq":[{"member":"category/contact/phone"},"1"]}}""", "not_filterable", "/filter/eq/0")]
    [InlineData("""{"filter":{"any":{"source":{"member":"lines"},"var":"l","body":{"eq":[{"member":"l/shipping/country"},"UA"]}}}}""", "not_filterable", "/filter/any/body/eq/0")]
    public async Task Invalid_value_paths_are_rejected(string query, string code, string location)
    {
        var problem = await shop.Server.QueryProblemAsync("products", "{\"v\":1," + query[1..]);

        JsonAssert.Problem(problem, code, location);
    }

    [Fact]
    public async Task Values_read_through_a_filtered_navigation_are_null_when_the_target_is_hidden()
    {
        await using var server = await ShopServer.StartAsync(shop.Database, options => options.AddPolicy<ProductPolicy>().AddPolicy<CategoryPolicy>());
        using (var scope = server.Services.CreateScope())
        {
            // Tenant 1's Notebook moves into tenant 2's Tools, whose contact tenant 1 must not see.
            var db = scope.ServiceProvider.GetRequiredService<ShopDbContext>();
            (await db.Products.FindAsync([5], TestContext.Current.CancellationToken))!.CategoryId = 3;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Query, "/api/products");
        request.Content = new StringContent("""
            {"v":1,"filter":{"eq":[{"member":"id"},5]},
             "select":{"new":{"contact":{"member":"category/contact"},"email":{"member":"category/contact/email"},
                              "has":{"ne":[{"member":"category/contact"},null]}}}}
            """, Encoding.UTF8, "application/json");
        request.Headers.Add("X-Tenant", "1");
        using var response = await server.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        JsonAssert.Equal("""{"contact":null,"email":null,"has":false}""", JsonAssert.Single(body));
    }
}

public sealed class SqliteValueTypeQueryTests(SqliteShopFixture shop) : ValueTypeQueryTests(shop), IClassFixture<SqliteShopFixture>;

public sealed class PostgresValueTypeQueryTests(PostgresShopFixture shop) : ValueTypeQueryTests(shop), IClassFixture<PostgresShopFixture>;

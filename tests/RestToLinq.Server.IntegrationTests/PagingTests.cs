using System.Net;
using System.Text.Json;

namespace RestToLinq.Server.IntegrationTests;

public abstract class PagingTests(ShopFixture shop)
{
    [Fact]
    public async Task Offset_pages_follow_the_sort_with_the_key_as_tiebreaker()
    {
        var all = await shop.Server.QueryOkAsync("products", """{"v":1,"orderBy":[{"member":"price","desc":true}]}""");
        var page = await shop.Server.QueryOkAsync("products", """{"v":1,"orderBy":[{"member":"price","desc":true}],"page":{"mode":"offset","skip":2,"take":2}}""");

        Assert.Equal([3, 1, 6, 4, 2, 5], JsonAssert.Ids(all));
        Assert.Equal([6, 4], JsonAssert.Ids(page));
        Assert.False(page.TryGetProperty("nextCursor", out _));
    }

    [Fact]
    public async Task Default_page_size_applies_without_take()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"ne":[{"member":"id"},0]}}""");

        Assert.Equal(6, JsonAssert.Ids(result).Length);
    }

    [Fact]
    public async Task Count_is_the_filtered_total_regardless_of_the_page()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"eq":[{"member":"category/name"},"Games"]},"page":{"mode":"offset","take":1},"count":true}""");
        var countOnly = await shop.Server.QueryOkAsync("products", """{"v":1,"page":{"mode":"offset","take":0},"count":true}""");

        Assert.Equal(3, result.GetProperty("totalCount").GetInt64());
        Assert.Single(JsonAssert.Ids(result));
        Assert.Empty(JsonAssert.Ids(countOnly));
        Assert.Equal(6, countOnly.GetProperty("totalCount").GetInt64());
    }

    [Theory]
    [InlineData("""[{"member":"price","desc":true}]""", new[] { 3, 1, 6, 4, 2, 5 })]
    [InlineData("""[{"member":"createdAt"}]""", new[] { 1, 4, 2, 3, 5, 6 })]
    [InlineData("""[{"member":"status"}]""", new[] { 3, 1, 2, 4, 6, 5 })]
    [InlineData("""[{"member":"price"},{"member":"id","desc":true}]""", new[] { 5, 2, 4, 6, 1, 3 })]
    public async Task Cursor_pages_walk_the_same_order_as_offset(string orderBy, int[] expected)
    {
        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var after = cursor is null ? "" : $",\"after\":\"{cursor}\"";
            var page = await shop.Server.QueryOkAsync("products", """{"v":1,"orderBy":""" + orderBy + ""","page":{"mode":"cursor","take":2""" + after + """},"count":true}""");
            seen.AddRange(JsonAssert.Ids(page));
            Assert.Equal(6, page.GetProperty("totalCount").GetInt64());
            cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(expected, seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Cursor_only_set_defaults_to_cursor_pages()
    {
        var first = await shop.Server.QueryOkAsync("categories", """{"v":1,"orderBy":[{"member":"name","desc":true}],"page":{"mode":"cursor","take":1}}""");
        var second = await shop.Server.QueryOkAsync("categories", """{"v":1,"orderBy":[{"member":"name","desc":true}],"page":{"mode":"cursor","take":5,"after":""" + "\"" + first.GetProperty("nextCursor").GetString() + "\"}}");
        var unpaged = await shop.Server.QueryOkAsync("categories", """{"v":1}""");

        Assert.Equal([3], JsonAssert.Ids(first));
        Assert.Equal([2, 1], JsonAssert.Ids(second));
        Assert.False(second.TryGetProperty("nextCursor", out _));
        Assert.Equal([1, 2, 3], JsonAssert.Ids(unpaged));
    }

    [Fact]
    public async Task Cursor_from_another_sort_is_rejected()
    {
        var page = await shop.Server.QueryOkAsync("products", """{"v":1,"orderBy":[{"member":"price"}],"page":{"mode":"cursor","take":2}}""");
        var cursor = page.GetProperty("nextCursor").GetString();

        var problem = await shop.Server.QueryProblemAsync("products", """{"v":1,"orderBy":[{"member":"createdAt"}],"page":{"mode":"cursor","after":""" + "\"" + cursor + "\"}}");

        JsonAssert.Problem(problem, "invalid_cursor", "/page/after");
    }

    public static readonly TheoryData<string, string, string, string> Rejected = new()
    {
        { "products", """{"v":1,"orderBy":[{"member":"name"}]}""", "not_sortable", "/orderBy/0/member" },
        { "products", """{"v":1,"orderBy":[{"member":"lines/quantity"}]}""", "unknown_member", "/orderBy/0/member" },
        { "products", """{"v":1,"orderBy":[{"member":"category"}]}""", "not_sortable", "/orderBy/0" },
        { "products", """{"v":1,"orderBy":[{"member":"releasedOn"}],"page":{"mode":"cursor"}}""", "cursor_nullable_sort", "/orderBy/0" },
        { "products", """{"v":1,"orderBy":[{"member":"category/name"}],"page":{"mode":"cursor"}}""", "cursor_nullable_sort", "/orderBy/0" },
        { "products", """{"v":1,"page":{"mode":"cursor","after":"e30"}}""", "invalid_cursor", "/page/after" },
        { "products", """{"v":1,"page":{"mode":"offset","take":21}}""", "limit_exceeded", "/page/take" },
        { "products", """{"v":1,"page":{"mode":"offset","skip":101}}""", "limit_exceeded", "/page/skip" },
        { "categories", """{"v":1,"page":{"mode":"offset"}}""", "invalid_query", "/page/mode" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task Invalid_sort_or_page_is_rejected(string set, string json, string code, string location)
    {
        var problem = await shop.Server.QueryProblemAsync(set, json);

        JsonAssert.Problem(problem, code, location);
    }

    [Fact]
    public async Task Sorting_through_a_navigation_works_in_offset_mode()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"ne":[{"member":"categoryId"},null]},"orderBy":[{"member":"category/name","desc":true}]}""");

        Assert.Equal([4, 1, 3, 6, 2], JsonAssert.Ids(result));
    }

    [Fact]
    public async Task Exceeding_limits_reports_the_limit_name()
    {
        var problem = await shop.Server.QueryProblemAsync("products", """{"v":1,"page":{"mode":"offset","take":21}}""");

        Assert.Equal("pageSize", problem.GetProperty("limit").GetString());
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.String, problem.GetProperty("title").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (HttpStatusCode)problem.GetProperty("status").GetInt32());
    }
}

public sealed class SqlitePagingTests(SqliteShopFixture shop) : PagingTests(shop), IClassFixture<SqliteShopFixture>;

public sealed class PostgresPagingTests(PostgresShopFixture shop) : PagingTests(shop), IClassFixture<PostgresShopFixture>;

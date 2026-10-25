using LinqToRest.Protocol;
using LinqToRest.Server.IntegrationTests.Shop;

namespace LinqToRest.Server.IntegrationTests;

/// <summary>
/// Null sorts smallest on every provider (first ascending, last descending), in offset, cursor and grouped queries.
/// SQLite already orders NULL that way; PostgreSQL gets an <c>IS NULL</c> sort key.
/// </summary>
public abstract class NullSortTests(ShopFixture shop)
{
    // releasedOn: null for 2, 4, 5. category/name: null for 5, Games for 1, 3, 6. origin/country: null for 4, 5, 6.
    public static readonly TheoryData<string, bool, int[]> Orders = new()
    {
        { "releasedOn", false, [2, 4, 5, 1, 3, 6] },
        { "releasedOn", true, [6, 3, 1, 2, 4, 5] },
        { "category/name", false, [5, 2, 1, 3, 6, 4] },
        { "category/name", true, [4, 1, 3, 6, 2, 5] },
        { "origin/country", false, [4, 5, 6, 3, 1, 2] },
        { "origin/country", true, [2, 1, 3, 4, 5, 6] },
    };

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Offset_pages_order_null_smallest(string member, bool descending, int[] expected)
    {
        var result = await shop.Server.QueryOkAsync("products", $$$"""{"v":1,"orderBy":[{"member":"{{{member}}}","desc":{{{Json(descending)}}}}],"page":{"mode":"offset"}}""");

        Assert.Equal(expected, JsonAssert.Ids(result));
    }

    [Theory]
    [MemberData(nameof(Orders))]
    public async Task Cursor_pages_walk_the_same_order_across_nulls_and_ties(string member, bool descending, int[] expected)
    {
        foreach (var take in new[] { 1, 2, 4 })
        {
            var seen = new List<int>();
            string? cursor = null;
            do
            {
                var after = cursor is null ? "" : $",\"after\":\"{cursor}\"";
                var page = await shop.Server.QueryOkAsync("products", $$$"""{"v":1,"orderBy":[{"member":"{{{member}}}","desc":{{{Json(descending)}}}}],"page":{"mode":"cursor","take":{{{take}}}{{{after}}}}}""");
                seen.AddRange(JsonAssert.Ids(page));
                cursor = page.TryGetProperty("nextCursor", out var next) ? next.GetString() : null;
            }
            while (cursor is not null);

            Assert.Equal(expected, seen);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_providers_ordering_null_largest_get_a_null_sort_key(bool descending)
    {
        shop.Server.Sql.Clear();

        // Offset mode, so no keyset mentions the member: any null check on it is the sort key.
        await shop.Server.QueryOkAsync("products", $$$"""{"v":1,"orderBy":[{"member":"releasedOn","desc":{{{Json(descending)}}}}],"page":{"mode":"offset"}}""");

        var sql = Assert.Single(shop.Server.Sql);
        Assert.Equal(shop.Database.Provider == TestProvider.Postgres, sql.Contains("\"ReleasedOn\" IS NULL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Required_sorts_get_no_null_handling()
    {
        var first = await shop.Server.QueryOkAsync("products", """{"v":1,"orderBy":[{"member":"price"}],"page":{"mode":"cursor","take":2}}""");
        shop.Server.Sql.Clear();

        await shop.Server.QueryOkAsync("products", $$$"""{"v":1,"orderBy":[{"member":"price"}],"page":{"mode":"cursor","take":2,"after":"{{{first.GetProperty("nextCursor").GetString()}}}"}}""");

        Assert.DoesNotContain("\"Price\" IS", Assert.Single(shop.Server.Sql), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, new[] { null, "Books", "Games", "Tools" })]
    [InlineData(true, new[] { "Tools", "Games", "Books", null })]
    public async Task Groups_order_null_smallest(bool descending, string?[] expected)
    {
        var result = await shop.Server.QueryOkAsync("products", $$$"""
            {"v":1,"groupBy":["category/name"],"select":{"new":{"name":{"key":0} } },"orderBy":[{"member":"name","desc":{{{Json(descending)}}}}]}
            """);

        Assert.Equal(expected, result.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("name").GetString()));
    }

    public static readonly TheoryData<string, string, object?[]> NullCursors = new()
    {
        // A required string (strings read as null whatever the member), the key, and a required value type.
        { "categories", "name", [null, 1] },
        { "products", "releasedOn", [null, null] },
        { "products", "price", [null, 1] },
    };

    [Theory]
    [MemberData(nameof(NullCursors))]
    public async Task Null_for_a_required_sort_member_is_an_invalid_cursor(string set, string member, object?[] values)
    {
        var cursor = Cursor.Encode(Cursor.HashSort(set, [(member, false), ("id", false)]), values);

        var problem = await shop.Server.QueryProblemAsync(set, $$$"""{"v":1,"orderBy":[{"member":"{{{member}}}"}],"page":{"mode":"cursor","after":"{{{cursor}}}"}}""");

        JsonAssert.Problem(problem, "invalid_cursor", "/page/after");
    }

    private static string Json(bool value) => value ? "true" : "false";
}

public sealed class SqliteNullSortTests(SqliteShopFixture shop) : NullSortTests(shop), IClassFixture<SqliteShopFixture>;

public sealed class PostgresNullSortTests(PostgresShopFixture shop) : NullSortTests(shop), IClassFixture<PostgresShopFixture>;

namespace LinqToRest.Server.IntegrationTests;

public abstract class FilterTests(ShopFixture shop)
{
    public static readonly TheoryData<string, int[]> Filters = new()
    {
        { """{"eq":[{"member":"name"},"Go"]}""", [3] },
        { """{"ne":[{"member":"status"},"Active"]}""", [3, 5] },
        { """{"gt":[{"member":"price"},15]}""", [1, 3, 6] },
        { """{"ge":[{"member":"price"},15]}""", [1, 3, 4, 6] },
        { """{"lt":[{"member":"price"},12.5]}""", [5] },
        { """{"le":[{"member":"price"},12.5]}""", [2, 5] },
        { """{"gt":[{"member":"name"},"Hammer"]}""", [5, 6] },
        { """{"lt":[15,{"member":"price"}]}""", [1, 3, 6] },
        { """{"in":[{"member":"name"},["Go","Dune","Nope"]]}""", [2, 3] },
        { """{"in":[{"member":"status"},["Draft","Retired"]]}""", [3, 5] },
        { """{"and":[{"eq":[{"member":"isFeatured"},true]},{"lt":[{"member":"price"},25]}]}""", [1, 6] },
        { """{"or":[{"eq":[{"member":"id"},1]},{"eq":[{"member":"id"},2]},{"eq":[{"member":"id"},3]}]}""", [1, 2, 3] },
        { """{"not":[{"eq":[{"member":"status"},"Active"]}]}""", [3, 5] },
        { """{"contains":[{"member":"name"},"u"]}""", [2, 6] },
        { """{"startsWith":[{"member":"name"},"Ch"]}""", [1] },
        { """{"endsWith":[{"member":"name"},"er"]}""", [4] },
        { """{"eq":[{"toLower":[{"member":"name"}]},"go"]}""", [3] },
        { """{"eq":[{"toUpper":[{"member":"name"}]},"GO"]}""", [3] },
        { """{"eq":[{"length":[{"member":"name"}]},2]}""", [3] },
        { """{"eq":[{"trim":[{"member":"description"}]},"A5 dotted"]}""", [5] },
        { """{"eq":[{"concat":[{"member":"name"},"!"]},"Go!"]}""", [3] },
        { """{"eq":[{"concat":[{"member":"name"},"#",{"member":"id"}]},"Go#3"]}""", [3] },
        { """{"gt":[{"mul":[{"member":"price"},2]},50]}""", [3] },
        { """{"eq":[{"add":[{"member":"id"},1]},4]}""", [3] },
        { """{"eq":[{"sub":[{"member":"id"},1]},4]}""", [5] },
        { """{"eq":[{"div":[{"member":"id"},2]},3]}""", [6] },
        { """{"eq":[{"mod":[{"member":"id"},2]},0]}""", [2, 4, 6] },
        { """{"lt":[{"neg":[{"member":"id"}]},-5]}""", [6] },
        { """{"eq":[{"year":[{"member":"createdAt"}]},2026]}""", [1, 2, 3, 4, 5, 6] },
        { """{"eq":[{"month":[{"member":"createdAt"}]},1]}""", [1, 4] },
        { """{"eq":[{"day":[{"member":"createdAt"}]},1]}""", [2, 5, 6] },
        { """{"eq":[{"hour":[{"member":"createdAt"}]},9]}""", [1] },
        { """{"eq":[{"minute":[{"member":"createdAt"}]},45]}""", [6] },
        { """{"eq":[{"date":[{"member":"createdAt"}]},"2026-01-10T00:00:00"]}""", [1] },
        { """{"gt":[{"member":"createdAt"},"2026-03-01T00:00:00"]}""", [3, 5, 6] },
        { """{"eq":[{"year":[{"member":"releasedOn"}]},2026]}""", [3, 6] },
        { """{"ge":[{"member":"releasedOn"},"2026-01-01"]}""", [3, 6] },
        { """{"eq":[{"member":"releasedOn"},null]}""", [2, 4, 5] },
        { """{"ne":[{"member":"description"},null]}""", [1, 5] },
        { """{"eq":[{"coalesce":[{"member":"description"},""]},""]}""", [2, 3, 4, 6] },
        { """{"eq":[{"if":[{"gt":[{"member":"price"},18]},"high","low"]},"high"]}""", [1, 3, 6] },
        { """{"eq":[{"member":"isFeatured"},true]}""", [1, 6] },
        { """{"eq":[{"member":"name"},null]}""", [] },
        { """{"member":"isFeatured"}""", [1, 6] },
        { """{"eq":[{"member":"version"},"00000003-0000-0000-0000-000000000000"]}""", [3] },
        { """{"any":{"source":{"member":"tags"},"var":"t","body":{"eq":[{"var":"t"},"sale"]}}}""", [1, 5] },
        { """{"any":{"source":{"member":"tags"},"var":"t","body":{"eq":[{"member":"t"},"sale"]}}}""", [1, 5] },
        { """{"any":{"source":{"member":"tags"}}}""", [1, 2, 3, 5] },
        { """{"all":{"source":{"member":"lines"},"var":"l","body":{"gt":[{"member":"l/quantity"},0]}}}""", [1, 2, 3, 5, 6] },
        { """{"gt":[{"count":{"source":{"member":"lines"}}},1]}""", [1] },
        { """{"eq":[{"count":{"source":{"member":"lines"},"var":"l","body":{"gt":[{"member":"l/quantity"},1]}}},1]}""", [1, 2] },
        { """{"gt":[{"sum":{"source":{"member":"lines"},"var":"l","body":{"mul":[{"member":"l/quantity"},{"member":"l/unitPrice"}]}}},50]}""", [1] },
        { """{"ge":[{"max":{"source":{"member":"lines"},"var":"l","body":{"member":"l/quantity"}}},3]}""", [2] },
        { """{"eq":[{"min":{"source":{"member":"lines"},"var":"l","body":{"member":"l/quantity"}}},0]}""", [4] },
        { """{"gt":[{"avg":{"source":{"member":"lines"},"var":"l","body":{"member":"l/quantity"}}},1.4]}""", [1, 2] },
        { """{"gt":[{"sum":{"source":{"member":"lines"},"var":"l","where":{"gt":[{"member":"l/quantity"},1]},"body":{"member":"l/quantity"}}},2]}""", [2] },
        { """{"eq":[{"member":"category/name"},"Games"]}""", [1, 3, 6] },
        { """{"eq":[{"member":"categoryName"},null]}""", [5] },
        { """{"any":{"source":{"member":"category/products"},"var":"p","body":{"eq":[{"member":"p/name"},"Dune"]}}}""", [2] },
        { """{"any":{"source":{"member":"category/products"},"var":"p","body":{"any":{"source":{"member":"p/lines"},"var":"l","body":{"eq":[{"member":"l/sku"},"H1"]}}}}}""", [4] },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task Filter_translates_and_returns_the_matching_rows(string filter, int[] expected)
    {
        var result = await shop.Server.QueryOkAsync("products", $$"""{"v":1,"filter":{{filter}}}""");

        Assert.Equal(expected, JsonAssert.Ids(result));
    }

    public static readonly TheoryData<string, string, string> Rejected = new()
    {
        { """{"eq":[{"member":"nope"},1]}""", "unknown_member", "/filter/eq/0" },
        { """{"eq":[{"member":"name/length"},1]}""", "unknown_member", "/filter/eq/0" },
        { """{"eq":[{"member":"lines/quantity"},1]}""", "unknown_member", "/filter/eq/0" },
        { """{"eq":[{"var":"q"},1]}""", "unknown_member", "/filter/eq/0" },
        { """{"gt":[{"member":"rating"},4]}""", "not_filterable", "/filter/gt/0" },
        { """{"eq":[{"member":"price"},"cheap"]}""", "type_mismatch", "/filter/eq/1" },
        { """{"eq":[{"member":"status"},"Unknown"]}""", "type_mismatch", "/filter/eq/1" },
        { """{"gt":[{"member":"isFeatured"},true]}""", "type_mismatch", "/filter" },
        { """{"eq":[{"member":"price"},{"member":"name"}]}""", "type_mismatch", "/filter" },
        { """{"contains":[{"member":"price"},"1"]}""", "type_mismatch", "/filter/contains/0" },
        { """{"member":"name"}""", "type_mismatch", "/filter" },
        { """{"eq":[{"member":"lines"},1]}""", "type_mismatch", "/filter/eq/0" },
        { """{"eq":[{"member":"category"},1]}""", "type_mismatch", "/filter/eq/0" },
        { """{"any":{"source":{"member":"name"}}}""", "type_mismatch", "/filter/any/source" },
        { """{"gt":[{"sum":{"source":{"member":"lines"}}},1]}""", "type_mismatch", "/filter/gt/0" },
        { """{"any":{"source":{"member":"lines"},"var":"rating","body":{"gt":[{"member":"rating"},4]}}}""", "type_mismatch", "/filter/any/body/gt/0" },
        { """{"any":{"source":{"member":"category/products"},"var":"p","body":{"any":{"source":{"member":"p/lines"},"var":"p"}}}}""", "invalid_query", "/filter/any/body" },
        { """{"in":[{"member":"price"},[1,"x"]]}""", "type_mismatch", "/filter/in/1/1" },
        { """{"eq":[{"year":[{"member":"name"}]},1]}""", "type_mismatch", "/filter/eq/0" },
        { """{"eq":[{"coalesce":[{"member":"price"},1]},1]}""", "type_mismatch", "/filter/eq/0" },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task Invalid_filter_is_rejected_with_code_and_pointer(string filter, string code, string location)
    {
        var problem = await shop.Server.QueryProblemAsync("products", $$"""{"v":1,"filter":{{filter}}}""");

        JsonAssert.Problem(problem, code, location);
    }
}

public sealed class SqliteFilterTests(SqliteShopFixture shop) : FilterTests(shop), IClassFixture<SqliteShopFixture>;

public sealed class PostgresFilterTests(PostgresShopFixture shop) : FilterTests(shop), IClassFixture<PostgresShopFixture>;

using System.Net;
using System.Text.Json;

namespace LinqToRest.Client.UnitTests;

public sealed class TranslationTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Where_order_and_offset_page_translate_to_one_query()
    {
        var (shop, http) = Shop.Create();

        await shop.Products
            .Where(p => p.Price > 10 && p.Name.StartsWith('C'))
            .OrderByDescending(p => p.Price)
            .ThenBy(p => p.Id)
            .Skip(2)
            .Take(5)
            .ToListAsync(Token);

        Assert.Equal((HttpMethod.Query, "/api/products"), (http.Requests[0].Method, http.Requests[0].Path));
        JsonEqual("""
            {"v":1,"filter":{"and":[{"gt":[{"member":"price"},10]},{"startsWith":[{"member":"name"},"C"]}]},
             "orderBy":[{"member":"price","desc":true},{"member":"id"}],"page":{"mode":"offset","skip":2,"take":5}}
            """, http.LastJson);
    }

    public static readonly TheoryData<string, string> Filters = new()
    {
        { "enum", """{"eq":[{"member":"status"},"Active"]}""" },
        { "captured", """{"and":[{"ge":[{"member":"price"},5]},{"in":[{"member":"name"},["a","b"]]},{"in":[{"member":"id"},[1,2]]}]}""" },
        { "navigation", """{"eq":[{"member":"category/name"},"Games"]}""" },
        { "enum list", """{"in":[{"member":"status"},["Draft","Active"]]}""" },
        { "any", """{"any":{"source":{"member":"tags"},"var":"v0","body":{"eq":[{"var":"v0"},"sale"]}}}""" },
        { "sum", """{"gt":[{"sum":{"source":{"member":"lines"},"var":"v0","where":{"gt":[{"member":"v0/quantity"},1]},"body":{"mul":[{"member":"v0/quantity"},{"member":"v0/unitPrice"}]}}},50]}""" },
        { "count", """{"and":[{"gt":[{"count":{"source":{"member":"lines"}}},1]},{"eq":[{"count":{"source":{"member":"lines"},"var":"v0","body":{"gt":[{"member":"v0/quantity"},0]}}},2]}]}""" },
        { "contains", """{"any":{"source":{"member":"tags"},"var":"v0","body":{"eq":[{"var":"v0"},"x"]}}}""" },
        { "strings", """{"and":[{"eq":[{"toLower":[{"member":"name"}]},{"toLower":["go"]}]},{"gt":[{"length":[{"trim":[{"member":"name"}]}]},1]},{"contains":[{"member":"name"},"o"]}]}""" },
        { "concat", """{"eq":[{"concat":[{"member":"name"},"!",{"member":"id"}]},"Go!3"]}""" },
        { "compare", """{"gt":[{"member":"name"},"H"]}""" },
        { "coalesce", """{"eq":[{"coalesce":[{"member":"description"},""]},""]}""" },
        { "conditional", """{"eq":[{"if":[{"gt":[{"member":"price"},18]},"high","low"]},"high"]}""" },
        { "dates", """{"and":[{"eq":[{"year":[{"member":"createdAt"}]},2026]},{"eq":[{"month":[{"member":"releasedOn"}]},3]},{"ne":[{"member":"releasedOn"},null]}]}""" },
        { "not", """{"not":[{"or":[{"eq":[{"member":"id"},1]},{"eq":[{"member":"id"},2]}]}]}""" },
        { "arithmetic", """{"eq":[{"mod":[{"neg":[{"member":"id"}]},2]},0]}""" },
        { "null", """{"eq":[{"member":"description"},null]}""" },
    };

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task Filter_translates(string name, string expected)
    {
        var (shop, http) = Shop.Create();
        var min = 5m;
        string[] names = ["a", "b"];
        List<int> ids = [1, 2];
        ProductStatus[] statuses = [ProductStatus.Draft, ProductStatus.Active];
        var query = name switch
        {
            "enum" => shop.Products.Where(p => p.Status == ProductStatus.Active),
            // ReSharper disable once CSharp14OverloadResolutionWithSpanBreakingChange — users write this; it binds to the span overload the translator must handle.
            "captured" => shop.Products.Where(p => p.Price >= min && names.Contains(p.Name) && ids.Contains(p.Id)),
            "navigation" => shop.Products.Where(p => p.Category!.Name == "Games"),
            // ReSharper disable once CSharp14OverloadResolutionWithSpanBreakingChange — enums bind to the span overload with a comparer.
            "enum list" => shop.Products.Where(p => statuses.Contains(p.Status)),
            "any" => shop.Products.Where(p => p.Tags.Any(t => t == "sale")),
            "sum" => shop.Products.Where(p => p.Lines.Where(l => l.Quantity > 1).Sum(l => l.Quantity * l.UnitPrice) > 50),
            "count" => shop.Products.Where(p => p.Lines.Count > 1 && p.Lines.Count(l => l.Quantity > 0) == 2),
            "contains" => shop.Products.Where(p => p.Tags.Contains("x")),
            "strings" => shop.Products.Where(p => p.Name.Equals("go", StringComparison.OrdinalIgnoreCase) && p.Name.Trim().Length > 1 && p.Name.Contains('o')),
            "concat" => shop.Products.Where(p => p.Name + "!" + p.Id == "Go!3"),
            "compare" => shop.Products.Where(p => string.CompareOrdinal(p.Name, "H") > 0),
            "coalesce" => shop.Products.Where(p => (p.Description ?? "") == ""),
            "conditional" => shop.Products.Where(p => (p.Price > 18 ? "high" : "low") == "high"),
            "dates" => shop.Products.Where(p => p.CreatedAt.Year == 2026 && p.ReleasedOn!.Value.Month == 3 && p.ReleasedOn.HasValue),
            "not" => shop.Products.Where(p => !(p.Id == 1 || p.Id == 2)),
            "arithmetic" => shop.Products.Where(p => -p.Id % 2 == 0),
            _ => shop.Products.Where(p => p.Description == null),
        };

        await query.Take(1).ToListAsync(Token);

        JsonEqual(expected, http.LastJson.GetProperty("filter"));
    }

    [Fact]
    public async Task Several_wheres_are_combined_with_and()
    {
        var (shop, http) = Shop.Create();

        await shop.Products.Where(p => p.Id > 1).Where(p => p.Id < 9).Take(1).ToListAsync(Token);

        JsonEqual("""{"and":[{"gt":[{"member":"id"},1]},{"lt":[{"member":"id"},9]}]}""", http.LastJson.GetProperty("filter"));
    }

    [Fact]
    public async Task Select_translates_shapes_aggregates_and_maps()
    {
        var (shop, http) = Shop.Create();

        await shop.Products.Select(p => new
        {
            p.Id,
            Total = p.Lines.Sum(l => l.Quantity * l.UnitPrice),
            Label = p.Name + " (" + p.CategoryName + ")",
            Skus = p.Lines.Where(l => l.Quantity > 1).Select(l => new { l.Sku }).ToList(),
            p.Category,
            Expensive = p.Price > 18,
        }).Take(1).ToListAsync(Token);

        JsonEqual("""
            {"new":{
              "id":{"member":"id"},
              "total":{"sum":{"source":{"member":"lines"},"var":"v0","body":{"mul":[{"member":"v0/quantity"},{"member":"v0/unitPrice"}]}}},
              "label":{"concat":[{"member":"name"}," (",{"member":"categoryName"},")"]},
              "skus":{"map":{"source":{"member":"lines"},"var":"v1","where":{"gt":[{"member":"v1/quantity"},1]},"body":{"new":{"sku":{"member":"v1/sku"}}}}},
              "category":{"member":"category"},
              "expensive":{"gt":[{"member":"price"},18]}}}
            """, http.LastJson.GetProperty("select"));
    }

    [Fact]
    public async Task Scalar_select_and_identity_select()
    {
        var (shop, http) = Shop.Create();

        await shop.Products.Select(p => p.Name).Take(1).ToListAsync(Token);
        var scalar = http.LastJson.GetProperty("select");
        await shop.Products.Select(p => p).Take(1).ToListAsync(Token);

        JsonEqual("""{"member":"name"}""", scalar);
        Assert.False(http.LastJson.TryGetProperty("select", out _));
    }

    [Fact]
    public async Task Include_translates_navigation_paths()
    {
        var (shop, http) = Shop.Create();

        await shop.Products.Include(p => p.Category).Include(p => p.Lines).Take(1).ToListAsync(Token);

        JsonEqual("""["category","lines"]""", http.LastJson.GetProperty("include"));
    }

    [Fact]
    public async Task Cursor_pages_translate_to_cursor_mode()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[],"nextCursor":"next","totalCount":7}""");

        var page = await shop.Products.OrderBy(p => p.Price).Take(3).ToCursorPageAsync(includeTotalCount: true, Token);
        JsonEqual("""{"mode":"cursor","take":3}""", http.LastJson.GetProperty("page"));
        Assert.True(http.LastJson.GetProperty("count").GetBoolean());
        await shop.Products.OrderBy(p => p.Price).After("next").Take(3).ToListAsync(Token);

        Assert.Equal(("next", 7L), (page.NextCursor, page.TotalCount));
        JsonEqual("""{"mode":"cursor","after":"next","take":3}""", http.LastJson.GetProperty("page"));
    }

    [Fact]
    public async Task Cursor_only_set_pages_with_cursors()
    {
        var (shop, http) = Shop.Create();

        await shop.Categories.Take(2).ToListAsync(Token);

        JsonEqual("""{"mode":"cursor","take":2}""", http.LastJson.GetProperty("page"));
    }

    [Fact]
    public async Task As_async_enumerable_follows_cursors_until_the_end()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[{"id":1,"name":"A"},{"id":2,"name":"B"}],"nextCursor":"c1"}""")
            .Respond("""{"items":[{"id":3,"name":"C"}]}""");

        var names = new List<string>();
        await foreach (var category in shop.Categories.OrderBy(c => c.Id).AsAsyncEnumerable(Token))
        {
            names.Add(category.Name);
        }

        Assert.Equal(["A", "B", "C"], names);
        Assert.Equal(2, http.Requests.Count);
        JsonEqual("""{"mode":"cursor","after":"c1","take":2}""", http.LastJson.GetProperty("page"));
    }

    [Fact]
    public async Task Count_asks_only_for_the_total_and_applies_skip_and_take()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[],"totalCount":10}""").Respond("""{"items":[],"totalCount":10}""");

        var all = await shop.Products.CountAsync(p => p.Price > 1, Token);
        JsonEqual("""{"v":1,"filter":{"gt":[{"member":"price"},1]},"select":{"new":{}},"page":{"mode":"offset","take":0},"count":true}""", http.LastJson);
        var paged = await shop.Products.Skip(8).Take(5).LongCountAsync(Token);

        Assert.Equal((10, 2L), (all, paged));
    }

    [Fact]
    public async Task Any_first_and_single_fetch_only_what_they_need()
    {
        var (shop, http) = Shop.Create();
        http.Respond("""{"items":[{}]}""")
            .Respond("""{"items":[{"id":1,"name":"A"}]}""")
            .Respond("""{"items":[{"id":1,"name":"A"},{"id":2,"name":"B"}]}""");

        Assert.True(await shop.Products.AnyAsync(p => p.Id > 0, Token));
        JsonEqual("""{"mode":"offset","take":1}""", http.LastJson.GetProperty("page"));
        Assert.Equal("A", (await shop.Products.FirstAsync(Token)).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shop.Products.SingleAsync(Token));
        JsonEqual("""{"mode":"offset","take":2}""", http.LastJson.GetProperty("page"));
        Assert.Null(await shop.Products.FirstOrDefaultAsync(Token));
    }

    public static readonly TheoryData<string> Unsupported =
    [
        "no take", "where after select", "skip after take", "order twice", "unknown method", "untranslatable member", "include with select",
        "group by", "skip on cursor-only set", "count after cursor", "cursor on offset-only set",
    ];

    [Theory]
    [MemberData(nameof(Unsupported))]
    public async Task Unsupported_queries_fail_locally_before_any_request(string name)
    {
        var (shop, http) = Shop.Create();
        Func<Task> run = name switch
        {
            "no take" => () => shop.Products.Where(p => p.Id > 0).ToListAsync(Token),
            "where after select" => () => shop.Products.Select(p => new { p.Id }).Where(p => p.Id > 0).Take(1).ToListAsync(Token),
            "skip after take" => () => shop.Products.Take(5).Skip(1).ToListAsync(Token),
            // ReSharper disable once MultipleOrderBy — the mistake under test.
            "order twice" => () => shop.Products.OrderBy(p => p.Price).OrderBy(p => p.Id).Take(1).ToListAsync(Token),
            "unknown method" => () => shop.Products.Where(p => p.Name.PadLeft(3) == "  a").Take(1).ToListAsync(Token),
            "untranslatable member" => () => shop.Products.Where(p => p.Name.GetHashCode() == 1).Take(1).ToListAsync(Token),
            "include with select" => () => shop.Products.Include(p => p.Category).Select(p => p.Name).Take(1).ToListAsync(Token),
            "group by" => () => shop.Products.GroupBy(p => p.Status).Select(g => g.Key).Take(1).ToListAsync(Token),
            "skip on cursor-only set" => () => shop.Categories.Skip(1).Take(1).ToListAsync(Token),
            "count after cursor" => () => shop.Products.After("c").CountAsync(Token),
            _ => () => shop.Set<OrderLineDto>().Take(1).ToCursorPageAsync(cancellationToken: Token),
        };

        await Assert.ThrowsAsync<NotSupportedException>(run);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Synchronous_enumeration_is_not_supported()
    {
        var (shop, http) = Shop.Create();

        Assert.Throws<NotSupportedException>(() => shop.Products.ToList());
        Assert.Throws<NotSupportedException>(() => shop.Products.First(p => p.Id > 1));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Untyped_query_creation_is_not_supported()
    {
        var (shop, _) = Shop.Create();

        var error = Assert.Throws<NotSupportedException>(() => shop.Products.Provider.CreateQuery(shop.Products.Expression));

        Assert.Contains("generic CreateQuery<TElement>", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_query_throws_with_the_problem_code()
    {
        var (shop, http) = Shop.Create();
        http.Problem(HttpStatusCode.BadRequest, """{"status":400,"code":"limit_exceeded","pointer":"/page/take","limit":"pageSize"}""");

        var error = await Assert.ThrowsAsync<RestQueryException>(() => shop.Products.Take(500).ToListAsync(Token));

        Assert.Equal((400, "limit_exceeded", "/page/take", "pageSize"), (error.Status, error.Code, error.Location, error.Limit));
    }

    [Fact]
    public async Task Non_problem_failure_throws_http_request_exception()
    {
        var (shop, http) = Shop.Create();
        http.Respond("oops", HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => shop.Products.Take(1).ToListAsync(Token));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    private static void JsonEqual(string expected, JsonElement actual)
    {
        using var document = JsonDocument.Parse(expected);
        Assert.True(JsonElement.DeepEquals(document.RootElement, actual), $"Expected {expected}{Environment.NewLine}Actual   {actual.GetRawText()}");
    }
}

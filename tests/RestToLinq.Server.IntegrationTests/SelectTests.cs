namespace RestToLinq.Server.IntegrationTests;

public abstract class SelectTests(ShopFixture shop)
{
    [Fact]
    public async Task Default_shape_is_the_full_dto_without_navigations()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"in":[{"member":"id"},[1,5]]}}""");

        var items = result.GetProperty("items");
        JsonAssert.Equal("""
            {"id":1,"name":"Chess","description":"Wooden board","price":20,"status":"Active",
             "version":"00000001-0000-0000-0000-000000000000","createdAt":"2026-01-10T09:30:00Z","releasedOn":"2025-12-01",
             "rating":4.5,"isFeatured":true,"categoryId":2,"tags":["classic","sale"],"categoryName":"Games"}
            """, items[0]);
        Assert.Equal("Notebook", items[1].GetProperty("name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, items[1].GetProperty("categoryName").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, items[1].GetProperty("categoryId").ValueKind);
    }

    [Fact]
    public async Task Include_adds_reference_and_collection_navigations()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"in":[{"member":"id"},[1,5]]},"include":["category","lines"]}""");

        var items = result.GetProperty("items");
        JsonAssert.Equal("""{"id":2,"name":"Games"}""", items[0].GetProperty("category"));
        JsonAssert.Equal("""
            [{"id":11,"productId":1,"sku":"C1","quantity":2,"unitPrice":20},{"id":12,"productId":1,"sku":"C2","quantity":1,"unitPrice":18}]
            """, items[0].GetProperty("lines"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, items[1].GetProperty("category").ValueKind);
        JsonAssert.Equal("[]", items[1].GetProperty("lines"));
    }

    [Fact]
    public async Task Nested_include_loads_navigations_of_navigations()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"eq":[{"member":"id"},2]},"include":["category/products"]}""");

        var category = JsonAssert.Single(result).GetProperty("category");
        Assert.Equal(["Dune"], category.GetProperty("products").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Select_projects_expressions_shapes_and_nested_collections()
    {
        var result = await shop.Server.QueryOkAsync("products", """
            {"v":1,"filter":{"eq":[{"member":"id"},1]},"select":{"new":{
              "id":{"member":"id"},
              "total":{"sum":{"source":{"member":"lines"},"var":"l","body":{"mul":[{"member":"l/quantity"},{"member":"l/unitPrice"}]}}},
              "label":{"concat":[{"member":"name"}," (",{"member":"category/name"},")"]},
              "bulk":{"map":{"source":{"member":"lines"},"var":"l","where":{"gt":[{"member":"l/quantity"},1]},"body":{"new":{"sku":{"member":"l/sku"}}}}},
              "quantities":{"map":{"source":{"member":"lines"},"var":"l","body":{"member":"l/quantity"}}},
              "category":{"member":"category"},
              "tags":{"member":"tags"},
              "lineCount":{"count":{"source":{"member":"lines"}}},
              "expensive":{"gt":[{"member":"price"},18]},
              "rating":{"member":"rating"},
              "meta":{"new":{"featured":{"member":"isFeatured"},"constant":"x"}}}}}
            """);

        JsonAssert.Equal("""
            {"id":1,"total":58,"label":"Chess (Games)","bulk":[{"sku":"C1"}],"quantities":[2,1],
             "category":{"id":2,"name":"Games"},"tags":["classic","sale"],"lineCount":2,"expensive":true,"rating":4.5,
             "meta":{"featured":true,"constant":"x"}}
            """, JsonAssert.Single(result));
    }

    [Fact]
    public async Task Select_expressions_run_in_sql()
    {
        await using var server = await ShopServer.StartAsync(shop.Database);
        server.Sql.Clear();

        await server.QueryOkAsync("products", """
            {"v":1,"select":{"new":{
              "total":{"sum":{"source":{"member":"lines"},"var":"l","body":{"mul":[{"member":"l/quantity"},{"member":"l/unitPrice"}]}}},
              "label":{"concat":[{"member":"name"}," (",{"member":"category/name"},")"]},
              "upper":{"toUpper":[{"member":"name"}]},
              "count":{"count":{"source":{"member":"tags"}}}}}}
            """);

        var sql = Assert.Single(server.Sql);
        // Providers spell functions differently (SUM / sum), so only the function matters.
        Assert.Contains("SUM(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("||", sql, StringComparison.Ordinal);
        Assert.Contains("upper(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Select_of_a_collection_navigation_returns_full_elements()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"eq":[{"member":"id"},2]},"select":{"new":{"lines":{"member":"lines"}}}}""");

        JsonAssert.Equal("""{"lines":[{"id":21,"productId":2,"sku":"D1","quantity":3,"unitPrice":12.5}]}""", JsonAssert.Single(result));
    }

    [Fact]
    public async Task Scalar_select_returns_bare_values()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"orderBy":[{"member":"price"}],"select":{"member":"name"},"page":{"mode":"offset","take":3}}""");

        Assert.Equal(["Notebook", "Dune", "Hammer"], result.GetProperty("items").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Missing_optional_navigation_projects_as_null()
    {
        var result = await shop.Server.QueryOkAsync("products", """{"v":1,"filter":{"eq":[{"member":"id"},5]},"select":{"new":{"category":{"member":"category"},"name":{"member":"category/name"}}}}""");

        JsonAssert.Equal("""{"category":null,"name":null}""", JsonAssert.Single(result));
    }

    [Theory]
    [InlineData("""{"v":1,"select":{"new":{"a":{"member":"nope"}}}}""", "unknown_member", "/select/new/a")]
    [InlineData("""{"v":1,"select":{"new":{"a":{"member":"id"}}},"include":["category"]}""", "invalid_query", "/include")]
    [InlineData("""{"v":1,"include":["nope"]}""", "unknown_member", "/include/0")]
    [InlineData("""{"v":1,"include":["name"]}""", "unknown_member", "/include/0")]
    [InlineData("""{"v":1,"select":{"map":{"source":{"member":"name"},"var":"x","body":1}}}""", "type_mismatch", "/select/map/source")]
    public async Task Invalid_select_is_rejected(string json, string code, string location)
    {
        var problem = await shop.Server.QueryProblemAsync("products", json);

        JsonAssert.Problem(problem, code, location);
    }
}

public sealed class SqliteSelectTests(SqliteShopFixture shop) : SelectTests(shop), IClassFixture<SqliteShopFixture>;

public sealed class PostgresSelectTests(PostgresShopFixture shop) : SelectTests(shop), IClassFixture<PostgresShopFixture>;

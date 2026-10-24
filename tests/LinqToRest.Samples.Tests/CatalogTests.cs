using System.Net;
using System.Net.Http.Json;
using System.Text;
using LinqToRest.Server.Hosting;
using Microsoft.Extensions.Hosting;

namespace LinqToRest.Samples.Tests;

/// <summary>What the catalog sample claims to show. Read-only checks share one API; writes get their own.</summary>
public sealed class CatalogTests(CatalogApi api) : IClassFixture<CatalogApi>
{
    private const string Count = """{"v":1,"page":{"mode":"offset","take":0},"count":true}""";

    [Fact]
    public async Task Anonymous_visitors_and_viewers_see_only_active_products()
    {
        var anonymous = await api.ClientAsync();
        var viewer = await api.ClientAsync("viewer");
        var editor = await api.ClientAsync("editor");

        Assert.Equal(170, (await anonymous.QueryOkAsync("products", Count)).Total());
        Assert.Equal(170, (await viewer.QueryOkAsync("products", Count)).Total());
        Assert.Equal(200, (await editor.QueryOkAsync("products", Count)).Total());
        Assert.Equal(0, (await anonymous.QueryOkAsync("products", """{"v":1,"filter":{"ne":[{"member":"status"},"Active"]},"page":{"mode":"offset","take":0},"count":true}""")).Total());
    }

    [Fact]
    public async Task Hidden_products_stay_hidden_through_navigations_images_and_counts()
    {
        var anonymous = await api.ClientAsync();
        var editor = await api.ClientAsync("editor");
        const string draft = """{"v":1,"filter":{"eq":[{"member":"sku"},"SKU-0002"]},"include":["images"]}""";
        const string brands = """
            {"v":1,"orderBy":[{"member":"name"}],
             "select":{"new":{"listed":{"member":"productCount"},
                              "visible":{"count":{"source":{"member":"products"}}}}}}
            """;

        var seenByEditor = (await editor.QueryOkAsync("products", draft)).GetProperty("items")[0];
        var draftImages = $$"""{"v":1,"filter":{"eq":[{"member":"productId"},{{seenByEditor.GetProperty("id").GetInt32()}}]},"page":{"mode":"offset","take":0},"count":true}""";

        Assert.Empty((await anonymous.QueryOkAsync("products", draft)).GetProperty("items").EnumerateArray());
        Assert.NotEmpty(seenByEditor.GetProperty("images").EnumerateArray());
        Assert.Equal(seenByEditor.GetProperty("images").GetArrayLength(), (await editor.QueryOkAsync("productImages", draftImages)).Total());
        Assert.Equal(0, (await anonymous.QueryOkAsync("productImages", draftImages)).Total());
        foreach (var brand in (await anonymous.QueryOkAsync("brands", brands)).GetProperty("items").EnumerateArray())
        {
            Assert.Equal(brand.GetProperty("visible").GetInt32(), brand.GetProperty("listed").GetInt32());
        }
    }

    [Theory]
    [InlineData("""{"v":1,"filter":{"gt":[{"member":"costPrice"},1]}}""")]
    [InlineData("""{"v":1,"filter":{"ne":[{"member":"internalNotes"},null]}}""")]
    [InlineData("""{"v":1,"select":{"member":"costPrice"}}""")]
    public async Task Internal_columns_are_unknown(string query)
    {
        var (status, problem) = await (await api.ClientAsync("editor")).QueryAsync("products", query);

        Assert.Equal((HttpStatusCode.BadRequest, "unknown_member"), (status, problem.Code()));
    }

    [Fact]
    public async Task Only_editors_write()
    {
        const string insert = """{"v":1,"operations":[{"id":"b","op":"insert","set":"brands","values":{"name":"Nope"}}]}""";

        var anonymous = await (await api.ClientAsync()).BatchAsync(insert);
        var viewer = await (await api.ClientAsync("viewer")).BatchAsync(insert);

        Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), (anonymous.Status, anonymous.Body.Code()));
        Assert.Equal((HttpStatusCode.Forbidden, "forbidden"), (viewer.Status, viewer.Body.Code()));
    }

    [Fact]
    public async Task Brands_can_never_be_deleted()
    {
        var (status, problem) = await (await api.ClientAsync("editor")).BatchAsync("""{"v":1,"operations":[{"id":"d","op":"delete","set":"brands","key":{"id":1}}]}""");

        Assert.Equal((HttpStatusCode.Forbidden, "operation_not_allowed", "/operations/0"), (status, problem.Code(), problem.GetProperty("pointer").GetString()));
    }

    [Fact]
    public async Task Cors_allows_a_query_preflight_from_a_configured_origin_only()
    {
        var client = await api.ClientAsync();

        using var allowed = await client.SendAsync(Preflight("/api/products", "QUERY", "http://localhost:5173"), TestContext.Current.CancellationToken);
        using var other = await client.SendAsync(Preflight("/api/products", "QUERY", "https://evil.example"), TestContext.Current.CancellationToken);

        Assert.Equal("http://localhost:5173", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("QUERY", allowed.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.Ordinal);
        Assert.Equal("600", Assert.Single(allowed.Headers.GetValues("Access-Control-Max-Age")));
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Cors_allows_the_dev_login_preflight()
    {
        using var response = await api.CreateClient().SendAsync(Preflight("/auth/dev-login", "POST", "http://localhost:5174"), TestContext.Current.CancellationToken);

        Assert.Equal("http://localhost:5174", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Cors_allows_a_cross_origin_query_response()
    {
        var client = await api.ClientAsync();
        using var request = new HttpRequestMessage(HttpMethod.Query, "/api/brands");
        request.Content = new StringContent("""{"v":1}""", Encoding.UTF8, "application/json");
        request.Headers.Add("Origin", "http://localhost:5173");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://localhost:5173", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Dev_login_rejects_unknown_roles()
    {
        using var response = await api.CreateClient().PostAsJsonAsync("/auth/dev-login", new { role = "admin" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_checked_in_model_equals_a_fresh_export()
    {
        _ = api.Services;
        var path = Path.Combine(Path.GetTempPath(), $"catalog-model-{Guid.NewGuid():N}.json");
        try
        {
            await api.Host!.ExportLinqToRestModelAsync(path, TestContext.Current.CancellationToken);
            var expected = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Catalog", "model.json"), TestContext.Current.CancellationToken);

            Assert.Equal(expected.ReplaceLineEndings("\n"), await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HttpRequestMessage Preflight(string path, string method, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
        return request;
    }
}

/// <summary>Writes and environment checks, each on its own API so the shared one stays as seeded.</summary>
public sealed class CatalogWriteTests
{
    [Fact]
    public async Task Editor_adds_a_product_with_images_in_one_batch()
    {
        await using var api = new CatalogApi();
        var editor = await api.ClientAsync("editor");

        var (status, body) = await editor.BatchAsync("""
            {"v":1,"operations":[
              {"id":"p","op":"insert","set":"products","values":{"sku":"SKU-9001","name":"Cube","price":9.5,"status":"Draft","stock":3,"categoryId":4,"brandId":1}},
              {"id":"i0","op":"insert","set":"productImages","values":{"url":"https://example.test/cube-0.png","sortOrder":0},"refs":{"productId":"p"}},
              {"id":"i1","op":"insert","set":"productImages","values":{"url":"https://example.test/cube-1.png","sortOrder":1},"refs":{"productId":"p"}}]}
            """);

        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
        var product = body.GetProperty("results")[0].GetProperty("value");
        Assert.Equal(2, product.GetProperty("imageCount").GetInt32());
        Assert.NotEqual(default, product.GetProperty("createdAt").GetDateTime());
        Assert.Equal(product.GetProperty("id").GetInt32(), body.GetProperty("results")[2].GetProperty("value").GetProperty("productId").GetInt32());
    }

    [Fact]
    public async Task Invalid_values_come_back_as_field_errors_before_anything_is_written()
    {
        await using var api = new CatalogApi();
        var editor = await api.ClientAsync("editor");

        // SKU-0002 exists: the validator reports it next to the SKU instead of the unique index's 409.
        var (status, problem) = await editor.BatchAsync("""
            {"v":1,"operations":[
              {"id":"p","op":"insert","set":"products","values":{"sku":"SKU-0002","name":"Cube","price":9.999,"status":"Draft","stock":-1,"categoryId":4,"brandId":1}},
              {"id":"i","op":"insert","set":"productImages","values":{"url":"http://example.test/cube.png","sortOrder":0},"refs":{"productId":"p"}}]}
            """);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "validation_failed"), (status, problem.Code()));
        Assert.Equal(
            [("p", "stock", "out_of_range"), ("p", "price", "too_precise"), ("p", "sku", "duplicate"), ("i", "url", "invalid_format")],
            problem.GetProperty("errors").EnumerateArray().Select(error => (error.GetProperty("operation").GetString(), error.GetProperty("member").GetString(), error.GetProperty("code").GetString())));
    }

    [Fact]
    public async Task Deleting_a_category_with_products_is_a_conflict()
    {
        await using var api = new CatalogApi();
        var editor = await api.ClientAsync("editor");

        var (status, problem) = await editor.BatchAsync("""{"v":1,"operations":[{"id":"d","op":"delete","set":"categories","key":{"id":4}}]}""");

        Assert.Equal((HttpStatusCode.Conflict, "conflict"), (status, problem.Code()));
    }

    [Fact]
    public async Task A_stale_edit_is_a_concurrency_conflict()
    {
        await using var api = new CatalogApi();
        var editor = await api.ClientAsync("editor");

        var (status, problem) = await editor.BatchAsync("""
            {"v":1,"operations":[{"id":"u","op":"update","set":"products","key":{"id":3},"values":{"price":1},"token":"00000000-0000-0000-0000-000000000001"}]}
            """);

        Assert.Equal((HttpStatusCode.PreconditionFailed, "concurrency_conflict"), (status, problem.Code()));
    }

    [Fact]
    public async Task Dev_login_exists_only_in_development()
    {
        await using var api = CatalogApi.InEnvironment(Environments.Production);

        using var response = await api.CreateClient().PostAsJsonAsync("/auth/dev-login", new { role = "editor" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

using System.Net;

namespace RestToLinq.Server.IntegrationTests;

public sealed class EndpointTests(ShopFixture shop) : IClassFixture<ShopFixture>
{
    [Fact]
    public async Task Unknown_set_is_not_found()
    {
        var problem = await shop.Server.QueryProblemAsync("nope", """{"v":1}""", HttpStatusCode.NotFound);

        JsonAssert.Problem(problem, "not_found", "");
    }

    [Fact]
    public async Task Non_json_body_is_unsupported()
    {
        using var response = await shop.Server.QueryAsync("products", """{"v":1}""", "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_json_is_rejected()
    {
        var problem = await shop.Server.QueryProblemAsync("products", """{"v":1,""");

        JsonAssert.Problem(problem, "invalid_json", "");
    }

    [Fact]
    public async Task Get_is_not_a_query()
    {
        using var response = await shop.Server.Client.GetAsync(new Uri("/api/products", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Problem_never_echoes_the_offending_value()
    {
        using var response = await shop.Server.QueryAsync("products", """{"v":1,"filter":{"eq":[{"member":"secretColumn"},"secretValue"]}}""");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Body_limit_is_enforced_without_content_length()
    {
        await using var server = await ShopServer.StartAsync(options => options.Limits(limits => limits.MaxBodyBytes = 16));
        using var content = new StreamContent(new MemoryStream("""{"v":1,"count":true}"""u8.ToArray()));
        content.Headers.ContentType = new("application/json");

        using var response = await server.Client.SendAsync(new HttpRequestMessage(HttpMethod.Query, "/api/products") { Content = content }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("bodySize", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}

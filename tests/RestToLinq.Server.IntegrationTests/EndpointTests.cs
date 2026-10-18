using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task Post_query_alias_answers_like_query()
    {
        const string json = """{"v":1,"filter":{"gt":[{"member":"price"},15]},"select":{"member":"name"},"page":{"mode":"offset","take":5}}""";
        var viaQuery = await shop.Server.QueryOkAsync("products", json);

        using var response = await shop.Server.Client.PostAsync(
            new Uri("/api/products/$query", UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(viaQuery.GetRawText(), body);
    }

    [Fact]
    public async Task Post_query_alias_enforces_the_same_rules()
    {
        using var response = await shop.Server.Client.PostAsync(
            new Uri("/api/products/$query", UriKind.Relative), new StringContent("""{"v":1,"orderBy":[{"member":"name"}]}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        using var form = await shop.Server.Client.PostAsync(
            new Uri("/api/products/$query", UriKind.Relative), new StringContent("v=1", Encoding.UTF8, "application/x-www-form-urlencoded"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not_sortable", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);
    }

    [Fact]
    public async Task Post_endpoints_are_described_for_openapi_and_query_is_not()
    {
        await using var server = await ShopServer.StartAsync(options => options.Services.AddEndpointsApiExplorer());

        var descriptions = server.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items.SelectMany(group => group.Items).ToList();

        Assert.Equal(["POST api/$batch", "POST api/{set}/$query"], descriptions.Select(d => $"{d.HttpMethod} {d.RelativePath}").Order(StringComparer.Ordinal));
        var batch = descriptions.Single(d => d.RelativePath == "api/$batch");
        Assert.Equal(typeof(Protocol.BatchRequest), Assert.Single(batch.ParameterDescriptions, p => p.Source.Id == "Body").Type);
        Assert.Contains(batch.SupportedResponseTypes, r => r.StatusCode == 412);
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using LinqToRest.Modeling;

namespace LinqToRest.Client.UnitTests;

public sealed class ShopContext(RestDataContextOptions<ShopContext> options) : RestDataContext(options)
{
    public DtoSet<ProductDto> Products => Set<ProductDto>();
    public DtoSet<CategoryDto> Categories => Set<CategoryDto>();
}

public static class Shop
{
    public static readonly DtoModel Model = TestModel.Create();

    public static (ShopContext Context, StubHandler Http) Create()
    {
        var http = new StubHandler();
        var client = new HttpClient(http) { BaseAddress = new Uri("https://shop.test/api/") };
        return (new ShopContext(new RestDataContextOptions<ShopContext>(Model, client)), http);
    }
}

/// <summary>Records requests and answers with queued responses (or an empty page).</summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body, string MediaType)> _responses = new();

    public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

    public string LastBody => Requests[^1].Body;

    public JsonElement LastJson => JsonDocument.Parse(LastBody).RootElement;

    public StubHandler Respond(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue((status, body, "application/json"));
        return this;
    }

    public void Problem(HttpStatusCode status, string problem) => _responses.Enqueue((status, problem, "application/problem+json"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        var (status, response, mediaType) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, """{"items":[]}""", "application/json");
        return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, mediaType) };
    }
}

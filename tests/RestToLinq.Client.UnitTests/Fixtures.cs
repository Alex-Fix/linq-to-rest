using System.Net;
using System.Text;
using System.Text.Json;
using RestToLinq.Modeling;

namespace RestToLinq.Client.UnitTests;

public enum ProductStatus
{
    Draft,
    Active,
}

public sealed class ProductDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public Guid Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateOnly? ReleasedOn { get; set; }
    public int? CategoryId { get; set; }
    public CategoryDto? Category { get; set; }
    public List<OrderLineDto> Lines { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public string? CategoryName { get; set; }
}

public sealed class CategoryDto : IDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class OrderLineDto : IDto
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

public sealed class ShopContext(RestDataContextOptions<ShopContext> options) : RestDataContext(options)
{
    public DtoSet<ProductDto> Products => Set<ProductDto>();
    public DtoSet<CategoryDto> Categories => Set<CategoryDto>();
}

public static class Shop
{
    public static readonly DtoModel Model = new DtoModelBuilder()
        .Dto<ProductDto>(b =>
        {
            b.ToSet("products").HasKey(d => d.Id).HasConcurrencyToken(d => d.Version);
            b.HasOne(d => d.Category).WithForeignKey(d => d.CategoryId);
            b.Property(d => d.Price).IsSortable();
            b.Property(d => d.Tags).IsReadOnly();
            b.Property(d => d.CategoryName).IsReadOnly();
        })
        .Dto<CategoryDto>(b => b.ToSet("categories").HasKey(d => d.Id).HasPagination(p => p.Modes(PaginationModes.Cursor).DefaultPageSize(2)))
        .Dto<OrderLineDto>(b => b.ToSet("orderLines").HasKey(d => d.Id).HasPagination(p => p.Modes(PaginationModes.Offset)))
        .Build();

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

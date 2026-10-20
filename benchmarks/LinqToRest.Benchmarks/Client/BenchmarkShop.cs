using System.Net;
using LinqToRest.Client;

namespace LinqToRest.Benchmarks.Client;

/// <summary>The context generated from tests/golden/model.json (Client/BenchmarkShop.generated.cs).</summary>
public sealed partial class BenchmarkShop
{
    /// <summary>A context whose HTTP calls answer with <paramref name="response"/>, without a network.</summary>
    public static BenchmarkShop Create(byte[] response) =>
        new(new RestDataContextOptions<BenchmarkShop>(Model, new HttpClient(new CannedHandler(response)) { BaseAddress = new Uri("https://shop.test/api/") }));

    private sealed class CannedHandler(byte[] response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(response);
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

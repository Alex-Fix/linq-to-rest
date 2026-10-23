using System.Net.Http.Json;
using Catalog.Client;
using LinqToRest.Client;

namespace Catalog.Blazor;

/// <summary>Who is signed in (through the sample's development-only login) and the contexts that carry the token.</summary>
public sealed class Session(IHttpClientFactory httpClients, IConfiguration configuration)
{
    public static readonly string ClientName = typeof(CatalogContext).FullName!;

    public string? Role { get; private set; }

    public string? Token { get; private set; }

    public bool IsEditor => Role == "editor";

    /// <summary>Raised after signing in or out, so pages re-run their queries.</summary>
    public event Action? Changed;

    /// <summary>A fresh unit of work; editing screens make one per save.</summary>
    public CatalogContext NewCatalog() =>
        new(new RestDataContextOptions<CatalogContext>(CatalogContext.Model, httpClients.CreateClient(ClientName)));

    public async Task SignInAsync(string? role)
    {
        if (role is null)
        {
            (Role, Token) = (null, null);
        }
        else
        {
            using var http = new HttpClient();
            http.BaseAddress = new Uri(configuration["CatalogApi"]!);
            using var response = await http.PostAsJsonAsync("auth/dev-login", new { role });
            response.EnsureSuccessStatusCode();
            var login = await response.Content.ReadFromJsonAsync<DevLogin>();
            (Role, Token) = (role, login!.Token);
        }

        Changed?.Invoke();
    }

    private sealed record DevLogin(string Token);
}

/// <summary>Adds the signed-in user's token to every LinqToRest request.</summary>
public sealed class BearerTokenHandler(Session session) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (session.Token is { } token)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

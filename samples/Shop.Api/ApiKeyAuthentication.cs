using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Shop.Api;

/// <summary>
/// A deliberately small API-key scheme for the sample: the X-Api-Key header maps to a tenant via configuration
/// (ApiKeys:&lt;key&gt; = &lt;tenant&gt;). Use your real authentication in an application.
/// </summary>
public sealed class ApiKeyAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    private const string TenantClaim = "tenant";

    public static string Tenant(ClaimsPrincipal user) =>
        user.FindFirstValue(TenantClaim) ?? throw new InvalidOperationException("The caller has no tenant.");

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var key) || configuration[$"ApiKeys:{key}"] is not { } tenant)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity([new Claim(TenantClaim, tenant)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

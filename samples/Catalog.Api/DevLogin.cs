using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Catalog.Api;

/// <summary>
/// A development-only sign-in: <c>POST /auth/dev-login {"role":"viewer"|"editor"}</c> returns a short-lived token
/// for a fixed user with that role. There is no user store and no password; a real app uses an identity provider.
/// </summary>
public static class DevLogin
{
    public const string Issuer = "catalog-sample";
    private static readonly string[] Roles = ["viewer", CatalogPolicy<Product>.EditorRole];
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>Accepts tokens signed with <c>DevLogin:SigningKey</c>; without a key no token is valid, so everyone is anonymous.</summary>
    public static void AddDevLoginAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        // Without a configured key, a random one nobody else knows: every token fails, everyone is anonymous.
        var key = SigningKey(configuration) ?? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = Issuer,
                IssuerSigningKey = key,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                NameClaimType = "sub",
                RoleClaimType = "role",
            };
        });
        services.AddAuthorization();
    }

    public static void MapDevLogin(this IEndpointRouteBuilder endpoints, IConfiguration configuration, TimeProvider time)
    {
        var key = SigningKey(configuration) ?? throw new InvalidOperationException("Configure DevLogin:SigningKey (appsettings.Development.json has one).");
        endpoints.MapPost("/auth/dev-login", (DevLoginRequest request) =>
        {
            if (Array.IndexOf(Roles, request.Role) < 0)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown role.", detail: "Use viewer or editor.");
            }

            var now = time.GetUtcNow().UtcDateTime;
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = Issuer,
                Subject = new ClaimsIdentity([new Claim("sub", request.Role), new Claim("role", request.Role)]),
                IssuedAt = now,
                NotBefore = now,
                Expires = now + Lifetime,
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            });
            return Results.Ok(new DevLoginResponse(token, request.Role, now + Lifetime));
        });
    }

    private static SymmetricSecurityKey? SigningKey(IConfiguration configuration) =>
        configuration["DevLogin:SigningKey"] is { Length: >= 32 } key ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)) : null;
}

public sealed record DevLoginRequest(string Role);

public sealed record DevLoginResponse(string Token, string Role, DateTime ExpiresAt);

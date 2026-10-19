using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace LinqToRest.Server.Policies;

/// <summary>The request a policy decides for.</summary>
public sealed class PolicyContext
{
    internal PolicyContext(HttpContext httpContext) => HttpContext = httpContext;

    /// <summary>Gets the HTTP context.</summary>
    public HttpContext HttpContext { get; }

    /// <summary>Gets the authenticated user.</summary>
    public ClaimsPrincipal User => HttpContext.User;

    /// <summary>Gets the request services.</summary>
    public IServiceProvider Services => HttpContext.RequestServices;

    /// <summary>Gets the token cancelled when the request is aborted.</summary>
    public CancellationToken RequestAborted => HttpContext.RequestAborted;
}

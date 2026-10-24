using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace LinqToRest.Server.Hosting;

/// <summary>Configures CORS for browser front ends on another origin.</summary>
public static class LinqToRestCorsPolicyBuilderExtensions
{
    private static readonly TimeSpan DefaultPreflightMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Allows what LinqToRest clients send: methods <c>QUERY</c>, <c>POST</c> and <c>GET</c>; headers
    /// <c>Content-Type</c>, <c>Authorization</c> and <c>If-None-Match</c>; exposes <c>ETag</c> (for <c>$model</c>);
    /// and lets browsers cache preflights, which every LinqToRest call needs. It adds to the policy, so further
    /// <c>WithHeaders(...)</c> calls keep these. It doesn't choose origins or credentials: call
    /// <c>WithOrigins(...)</c>, and <c>AllowCredentials()</c> for cookie authentication, yourself.
    /// </summary>
    /// <param name="policy">The policy builder.</param>
    /// <param name="preflightMaxAge">How long browsers may cache a preflight; 10 minutes by default.</param>
    /// <returns>The same policy builder.</returns>
    public static CorsPolicyBuilder AllowLinqToRest(this CorsPolicyBuilder policy, TimeSpan? preflightMaxAge = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var maxAge = preflightMaxAge ?? DefaultPreflightMaxAge;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxAge, TimeSpan.Zero, nameof(preflightMaxAge));

        return policy
            .WithMethods(HttpMethods.Query, HttpMethods.Post, HttpMethods.Get)
            .WithHeaders(HeaderNames.ContentType, HeaderNames.Authorization, HeaderNames.IfNoneMatch)
            .WithExposedHeaders(HeaderNames.ETag)
            .SetPreflightMaxAge(maxAge);
    }
}

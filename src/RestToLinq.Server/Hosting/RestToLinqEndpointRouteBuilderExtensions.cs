using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace RestToLinq.Server.Hosting;

/// <summary>Maps the RestToLinq endpoints.</summary>
public static class RestToLinqEndpointRouteBuilderExtensions
{
    private static readonly string[] QueryMethod = [HttpMethods.Query];

    /// <summary>
    /// Maps <c>QUERY {prefix}/{set}</c> and <c>POST {prefix}/$batch</c>. Returns the route group, so standard endpoint conventions such as
    /// <c>RequireAuthorization()</c> apply to every RestToLinq endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix, for example <c>/api</c>.</param>
    /// <returns>The route group.</returns>
    public static RouteGroupBuilder MapRestToLinq(this IEndpointRouteBuilder endpoints, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix);
        group.MapMethods("{set}", QueryMethod, QueryEndpoint.HandleAsync);
        group.MapPost("$batch", BatchEndpoint.HandleAsync);
        return group;
    }
}

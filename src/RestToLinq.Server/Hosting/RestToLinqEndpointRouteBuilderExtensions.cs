using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RestToLinq.Protocol;

namespace RestToLinq.Server.Hosting;

/// <summary>Maps the RestToLinq endpoints.</summary>
public static class RestToLinqEndpointRouteBuilderExtensions
{
    private const string Tag = "RestToLinq";
    private const string QueryDescription =
        "Runs a query against a set. The body is a RestToLinq query tree, for example "
        + """{"v":1,"filter":{"gt":[{"member":"price"},10]},"orderBy":[{"member":"price","desc":true}],"page":{"mode":"offset","take":20}}. """
        + "Same as QUERY {set}, for clients and proxies that can't send HTTP QUERY. The response is {\"items\":[...],\"nextCursor\"?,\"totalCount\"?}.";

    private static readonly string[] QueryMethod = [HttpMethods.Query];

    /// <summary>
    /// Maps <c>QUERY {prefix}/{set}</c>, its equivalent <c>POST {prefix}/{set}/$query</c> (described in OpenAPI, which
    /// can't describe QUERY), and <c>POST {prefix}/$batch</c>. Returns the route group, so standard endpoint
    /// conventions such as <c>RequireAuthorization()</c> apply to every RestToLinq endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">The route prefix, for example <c>/api</c>.</param>
    /// <returns>The route group.</returns>
    public static RouteGroupBuilder MapRestToLinq(this IEndpointRouteBuilder endpoints, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix);
        group.MapMethods("{set}", QueryMethod, QueryEndpoint.HandleAsync)
            .ExcludeFromDescription();

        group.MapPost("{set}/$query", QueryEndpoint.HandleAsync)
            .WithName("RestToLinqQuery")
            .WithTags(Tag)
            .WithSummary("Query a set")
            .WithDescription(QueryDescription)
            .Accepts<JsonElement>("application/json")
            .Produces<JsonElement>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        // A parameter besides HttpContext keeps this a described endpoint rather than a bare RequestDelegate.
        group.MapPost("$batch", (HttpRequest request) => BatchEndpoint.HandleAsync(request.HttpContext))
            .WithName("RestToLinqBatch")
            .WithTags(Tag)
            .WithSummary("Save a batch")
            .WithDescription("Applies inserts, updates and deletes atomically. Each operation names a set; updates and deletes need the key and concurrency token. Results are re-read through each set's projection.")
            .Accepts<BatchRequest>("application/json")
            .Produces<BatchResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
        return group;
    }
}

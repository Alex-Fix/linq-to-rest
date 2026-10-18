using Microsoft.AspNetCore.Http;
using RestToLinq.Server.Query;

namespace RestToLinq.Server.Hosting;

internal static class Problems
{
    /// <summary>Writes a problem response with constant title, stable code and JSON pointer, never request values.</summary>
    public static Task WriteAsync(HttpContext context, RequestException error)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code, ["pointer"] = error.Location };
        if (error.Limit is not null)
        {
            extensions["limit"] = error.Limit;
        }

        if (error.OperationIds is { Count: 1 } single)
        {
            extensions["operationId"] = single[0];
        }
        else if (error.OperationIds is { Count: > 1 } several)
        {
            extensions["operationIds"] = several;
        }

        var title = error.Status switch
        {
            StatusCodes.Status403Forbidden => "Forbidden.",
            StatusCodes.Status404NotFound => "Not found.",
            StatusCodes.Status409Conflict => "Conflict.",
            StatusCodes.Status412PreconditionFailed => "Concurrency conflict.",
            StatusCodes.Status415UnsupportedMediaType => "Unsupported media type.",
            _ => "Invalid request.",
        };

        return TypedResults.Problem(statusCode: error.Status, title: title, type: $"urn:resttolinq:error:{error.Code}", extensions: extensions)
            .ExecuteAsync(context);
    }
}

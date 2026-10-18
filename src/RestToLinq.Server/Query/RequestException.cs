using Microsoft.AspNetCore.Http;
using RestToLinq.Protocol;

namespace RestToLinq.Server.Query;

/// <summary>A rejected request, written as a problem response with a stable code and never the offending value.</summary>
internal sealed class RequestException(int status, string code, string location, string? limit = null, IReadOnlyList<string>? operationIds = null)
    : Exception($"{code} at '{location}'")
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public string Location { get; } = location;

    public string? Limit { get; } = limit;

    public IReadOnlyList<string>? OperationIds { get; } = operationIds;

    public static RequestException From(ProtocolException exception, IReadOnlyList<string>? operationIds = null) =>
        new(StatusCodes.Status400BadRequest, exception.Code, exception.Location, exception.Limit, operationIds);

    public static RequestException Invalid(string code, string location) => new(StatusCodes.Status400BadRequest, code, location);

    public static RequestException LimitExceeded(string location, string limit) =>
        new(StatusCodes.Status400BadRequest, ErrorCodes.LimitExceeded, location, limit);
}

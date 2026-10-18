using System.Net;
using System.Text.Json;
using RestToLinq.Client.Tracking;

namespace RestToLinq.Client;

internal static class Problems
{
    public static async Task<Exception> QueryFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (await ReadAsync(response, cancellationToken).ConfigureAwait(false) is not { } problem)
        {
            return Unexpected(response);
        }

        return new RestQueryException((int)response.StatusCode, problem.Code, problem.Location, problem.Limit);
    }

    public static async Task<Exception> SaveFailureAsync(HttpResponseMessage response, Dictionary<string, Entry> operations, CancellationToken cancellationToken)
    {
        if (await ReadAsync(response, cancellationToken).ConfigureAwait(false) is not { } problem)
        {
            return Unexpected(response);
        }

        var entities = problem.OperationIds.Select(id => operations.GetValueOrDefault(id)?.Entity).OfType<object>().ToList();
        return response.StatusCode == HttpStatusCode.PreconditionFailed
            ? new RestConcurrencyException((int)response.StatusCode, problem.Code, problem.Location, entities)
            : new RestSaveException((int)response.StatusCode, problem.Code, problem.Location, entities);
    }

    private static HttpRequestException Unexpected(HttpResponseMessage response) =>
        new($"The RestToLinq API answered {(int)response.StatusCode} without a problem response.", null, response.StatusCode);

    private static async Task<Problem?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/problem+json")
        {
            return null;
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var ids = root.TryGetProperty("operationIds", out var many) && many.ValueKind == JsonValueKind.Array
            ? [.. many.EnumerateArray().Select(id => id.GetString()).OfType<string>()]
            : Text("operationId") is { } one ? [one] : new List<string>();
        return new Problem(code.GetString()!, Text("pointer"), Text("limit"), ids);
    }

    private sealed record Problem(string Code, string? Location, string? Limit, IReadOnlyList<string> OperationIds);
}

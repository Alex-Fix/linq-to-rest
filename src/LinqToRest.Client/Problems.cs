using System.Globalization;
using System.Net;
using System.Text.Json;
using LinqToRest.Client.Tracking;
using LinqToRest.Protocol;

namespace LinqToRest.Client;

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

    public static async Task<Exception> SaveFailureAsync(
        HttpResponseMessage response, IReadOnlyList<Entry> ordered, Dictionary<string, Entry> operations, CancellationToken cancellationToken)
    {
        if (await ReadAsync(response, cancellationToken).ConfigureAwait(false) is not { } problem)
        {
            return Unexpected(response);
        }

        var status = (int)response.StatusCode;
        if (problem.Issues is { } issues)
        {
            // Server names map back to tracked entities and their CLR members.
            var errors = issues
                .Where(issue => issue.Operation is not null && operations.ContainsKey(issue.Operation))
                .Select(issue =>
                {
                    var entry = operations[issue.Operation!];
                    return new RestValidationError(entry.Entity, issue.Member is null ? null : entry.Type.FindProperty(issue.Member)?.Name ?? issue.Member, issue.Code);
                })
                .ToList();
            return new RestValidationException(status, problem.Location, errors, isLocal: false, isTruncated: issues.Any(issue => issue.Code == ErrorCodes.TooManyErrors));
        }

        var entities = problem.OperationIds.Select(id => operations.GetValueOrDefault(id)?.Entity).OfType<object>().ToList();
        return response.StatusCode == HttpStatusCode.PreconditionFailed
            ? new RestConcurrencyException(status, problem.Code, problem.Location, entities)
            : new RestSaveException(status, problem.Code, problem.Location, entities, Member(problem.Location, ordered));
    }

    /// <summary>The CLR member a pointer such as <c>/operations/2/values/price</c> names, if it names one.</summary>
    private static string? Member(string? location, IReadOnlyList<Entry> ordered)
    {
        var parts = location?.Split('/');
        return parts is ["", "operations", var index, "values", var member] && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position < ordered.Count
            ? ordered[position].Type.FindProperty(member)?.Name
            : null;
    }

    private static HttpRequestException Unexpected(HttpResponseMessage response) =>
        new($"The LinqToRest API answered {(int)response.StatusCode} without a problem response.", null, response.StatusCode);

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
        var issues = code.GetString() == ErrorCodes.ValidationFailed && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            ? BatchJson.ReadIssues(errors)
            : null;
        return new Problem(code.GetString()!, Text("pointer"), Text("limit"), ids, issues);
    }

    private sealed record Problem(string Code, string? Location, string? Limit, IReadOnlyList<string> OperationIds, IReadOnlyList<ValidationIssue>? Issues);
}

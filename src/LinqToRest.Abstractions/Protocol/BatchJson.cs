using System.Text.Json;

namespace LinqToRest.Protocol;

/// <summary>Reads and writes batch bodies (<c>POST /$batch</c>) and their responses.</summary>
public static class BatchJson
{
    /// <summary>Reads a batch body, enforcing the batch limits of <paramref name="limits"/>.</summary>
    /// <param name="utf8Json">The UTF-8 body.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <returns>The batch.</returns>
    /// <exception cref="ProtocolException">The body is invalid or exceeds a limit.</exception>
    public static BatchRequest Read(ReadOnlyMemory<byte> utf8Json, QueryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (utf8Json.Length > limits.MaxBatchBodyBytes)
        {
            throw new ProtocolException(ErrorCodes.LimitExceeded, "", "bodySize");
        }

        BatchRequest? batch;
        try
        {
            batch = JsonSerializer.Deserialize(utf8Json.Span, BatchJsonContext.Default.BatchRequest);
        }
        catch (JsonException)
        {
            throw new ProtocolException(ErrorCodes.InvalidJson, "");
        }

        if (batch is null)
        {
            throw new ProtocolException(ErrorCodes.InvalidBatch, "");
        }

        if (batch.Version != QueryJson.Version)
        {
            throw new ProtocolException(ErrorCodes.UnsupportedVersion, "/v");
        }

        if (batch.Operations.Count == 0)
        {
            throw new ProtocolException(ErrorCodes.InvalidBatch, "/operations");
        }

        if (batch.Operations.Count > limits.MaxBatchOperations)
        {
            throw new ProtocolException(ErrorCodes.LimitExceeded, "/operations", "batchOperations");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < batch.Operations.Count; index++)
        {
            var operation = batch.Operations[index];

            // System.Text.Json doesn't enforce nullability of collection elements, so "[null]" arrives as null.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (operation is null)
            {
                throw new ProtocolException(ErrorCodes.InvalidBatch, $"/operations/{index}");
            }

            if (!QueryJson.IsIdentifier(operation.Id) || ids.Contains(operation.Id))
            {
                throw new ProtocolException(ErrorCodes.InvalidBatch, $"/operations/{index}/id");
            }

            CheckShape(operation, $"/operations/{index}", ids);
            ids.Add(operation.Id);
        }

        return batch;
    }

    /// <summary>
    /// Link and unlink name a parent (key or an earlier insert), a navigation and an item, and nothing else; other
    /// operations never carry those members.
    /// </summary>
    private static void CheckShape(BatchOperation operation, string location, HashSet<string> earlier)
    {
        if (operation.Op is not (BatchOperationKind.Link or BatchOperationKind.Unlink))
        {
            var extra = operation.Ref is not null ? "ref" : operation.Navigation is not null ? "navigation" : operation.Item is not null ? "item" : null;
            if (extra is not null)
            {
                throw new ProtocolException(ErrorCodes.InvalidBatch, $"{location}/{extra}");
            }

            return;
        }

        var invalid = operation.Values is not null ? "values" : operation.Refs is not null ? "refs" : operation.Token is not null ? "token" : null;
        if (invalid is not null)
        {
            throw new ProtocolException(ErrorCodes.InvalidBatch, $"{location}/{invalid}");
        }

        if (operation.Navigation is null || !QueryJson.IsIdentifier(operation.Navigation))
        {
            throw new ProtocolException(ErrorCodes.InvalidBatch, $"{location}/navigation");
        }

        CheckRow(operation.Key, operation.Ref, $"{location}/key", $"{location}/ref", earlier);
        var item = operation.Item ?? throw new ProtocolException(ErrorCodes.InvalidBatch, $"{location}/item");
        CheckRow(item.Key, item.Ref, $"{location}/item", $"{location}/item/ref", earlier);
    }

    /// <summary>Exactly one of a key or a ref, and a ref names an earlier operation.</summary>
    private static void CheckRow(IReadOnlyDictionary<string, JsonElement>? key, string? reference, string location, string referenceLocation, HashSet<string> earlier)
    {
        if ((key is null) == (reference is null))
        {
            throw new ProtocolException(ErrorCodes.InvalidBatch, location);
        }

        if (reference is not null && !earlier.Contains(reference))
        {
            throw new ProtocolException(ErrorCodes.InvalidBatch, referenceLocation);
        }
    }

    /// <summary>Writes a batch body.</summary>
    /// <param name="batch">The batch.</param>
    /// <returns>The UTF-8 body.</returns>
    public static byte[] Write(BatchRequest batch) => JsonSerializer.SerializeToUtf8Bytes(batch, BatchJsonContext.Default.BatchRequest);

    /// <summary>Reads a batch response.</summary>
    /// <param name="utf8Json">The UTF-8 body.</param>
    /// <returns>The response.</returns>
    public static BatchResponse ReadResponse(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, BatchJsonContext.Default.BatchResponse)
        ?? throw new JsonException("The batch response is empty.");

    /// <summary>Writes a batch response.</summary>
    /// <param name="response">The response.</param>
    /// <returns>The UTF-8 body.</returns>
    public static byte[] WriteResponse(BatchResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, BatchJsonContext.Default.BatchResponse);

    /// <summary>Writes the <c>errors</c> member of a <c>validation_failed</c> problem.</summary>
    /// <param name="issues">The issues.</param>
    /// <returns>The JSON array.</returns>
    public static JsonElement WriteIssues(IEnumerable<ValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var json = JsonSerializer.SerializeToUtf8Bytes([.. issues], BatchJsonContext.Default.ListValidationIssue);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Reads the <c>errors</c> member of a <c>validation_failed</c> problem.</summary>
    /// <param name="errors">The JSON array.</param>
    /// <returns>The issues.</returns>
    /// <exception cref="JsonException">The array doesn't fit the contract.</exception>
    public static IReadOnlyList<ValidationIssue> ReadIssues(JsonElement errors) =>
        errors.Deserialize(BatchJsonContext.Default.ListValidationIssue) ?? throw new JsonException("The validation errors are missing.");
}

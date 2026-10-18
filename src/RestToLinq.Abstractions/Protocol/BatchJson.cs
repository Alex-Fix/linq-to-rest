using System.Text.Json;

namespace RestToLinq.Protocol;

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

            if (!QueryJson.IsIdentifier(operation.Id) || !ids.Add(operation.Id))
            {
                throw new ProtocolException(ErrorCodes.InvalidBatch, $"/operations/{index}/id");
            }
        }

        return batch;
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
}

using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinqToRest.Protocol;

/// <summary>
/// The opaque cursor of keyset pagination: base64url of <c>{"v":1,"s":&lt;sort hash&gt;,"k":[&lt;sort values&gt;]}</c>.
/// Cursors aren't signed: they only move the comparison point on visible, sortable members.
/// </summary>
public static class Cursor
{
    /// <summary>Hashes a sort specification, so a cursor can only continue the sort it came from.</summary>
    /// <param name="setName">The set name.</param>
    /// <param name="sort">The sort member paths and directions, including the appended key.</param>
    /// <returns>A short base64url hash.</returns>
    public static string HashSort(string setName, IEnumerable<(string Path, bool Descending)> sort)
    {
        ArgumentNullException.ThrowIfNull(setName);
        ArgumentNullException.ThrowIfNull(sort);
        var text = new StringBuilder(setName);
        foreach (var (path, descending) in sort)
        {
            text.Append('|').Append(path).Append(descending ? ":d" : ":a");
        }

        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 12));
    }

    /// <summary>Encodes a cursor.</summary>
    /// <param name="sortHash">The hash from <see cref="HashSort"/>.</param>
    /// <param name="values">The sort values of the last row, in sort order.</param>
    /// <returns>The cursor.</returns>
    public static string Encode(string sortHash, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", QueryJson.Version);
            writer.WriteString("s", sortHash);
            writer.WriteStartArray("k");
            foreach (var value in values)
            {
                JsonValues.Write(writer, value);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Base64Url.EncodeToString(buffer.WrittenSpan);
    }

    /// <summary>Decodes a cursor, checking its version, sort hash, value count and value types.</summary>
    /// <param name="cursor">The cursor.</param>
    /// <param name="sortHash">The hash of the current query's sort.</param>
    /// <param name="types">The sort member types, in sort order.</param>
    /// <param name="location">The JSON pointer reported on failure.</param>
    /// <returns>The sort values.</returns>
    /// <exception cref="ProtocolException">The cursor is invalid (code <see cref="ErrorCodes.InvalidCursor"/>).</exception>
    public static IReadOnlyList<object?> Decode(string cursor, string sortHash, IReadOnlyList<Type> types, string location)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(types);
        var invalid = new ProtocolException(ErrorCodes.InvalidCursor, location);
        byte[] json;
        try
        {
            json = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            throw invalid;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != QueryJson.Version
                || !root.TryGetProperty("s", out var hash) || hash.ValueKind != JsonValueKind.String || !hash.ValueEquals(sortHash)
                || !root.TryGetProperty("k", out var keys) || keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() != types.Count)
            {
                throw invalid;
            }

            var values = new object?[types.Count];
            for (var index = 0; index < types.Count; index++)
            {
                values[index] = JsonValues.ReadScalar(keys[index], types[index], location);
            }

            return values;
        }
        catch (Exception exception) when (exception is JsonException or ProtocolException)
        {
            throw invalid;
        }
    }
}

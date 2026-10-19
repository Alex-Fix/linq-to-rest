using LinqToRest.Protocol;
using LinqToRest.Server.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace LinqToRest.Server.Hosting;

internal static class RequestBody
{
    /// <summary>Reads a JSON body of at most <paramref name="maxBytes"/> bytes, without buffering more than that.</summary>
    public static async Task<ReadOnlyMemory<byte>> ReadAsync(HttpRequest request, int maxBytes)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
            || !mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new RequestException(StatusCodes.Status415UnsupportedMediaType, ErrorCodes.UnsupportedMediaType, "");
        }

        if (request.ContentLength > maxBytes)
        {
            throw RequestException.LimitExceeded("", "bodySize");
        }

        var buffer = new byte[Math.Min(maxBytes + 1, (int)(request.ContentLength ?? 4096) + 1)];
        var length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length > maxBytes)
                {
                    throw RequestException.LimitExceeded("", "bodySize");
                }

                Array.Resize(ref buffer, Math.Min(buffer.Length * 2, maxBytes + 1));
            }

            var read = await request.Body.ReadAsync(buffer.AsMemory(length), request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.AsMemory(0, length);
            }

            length += read;
            if (length > maxBytes)
            {
                throw RequestException.LimitExceeded("", "bodySize");
            }
        }
    }
}

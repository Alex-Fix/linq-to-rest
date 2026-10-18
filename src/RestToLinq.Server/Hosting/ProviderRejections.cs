using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RestToLinq.Protocol;
using RestToLinq.Server.Query;

namespace RestToLinq.Server.Hosting;

/// <summary>
/// Turns a database provider rejecting a client value (Npgsql refusing an offset-less DateTime for timestamptz, for
/// example) into a 400 instead of a 500. Only wraps execution, so the library's own bugs elsewhere still surface.
/// </summary>
internal static partial class ProviderRejections
{
    public static async Task<T> GuardAsync<T>(HttpContext context, string set, Func<Task<T>> execute)
    {
        try
        {
            return await execute().ConfigureAwait(false);
        }
        catch (InvalidCastException)
        {
            LogRejected(QueryEndpoint.Logger(context), set);
            throw new RequestException(StatusCodes.Status400BadRequest, ErrorCodes.TypeMismatch, "");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database provider rejected a value of a RestToLinq request for '{Set}'; answered 400 type_mismatch. Check the column types and value converters of that set.")]
    private static partial void LogRejected(ILogger logger, string set);
}

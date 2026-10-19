using LinqToRest.Protocol;
using LinqToRest.Server.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LinqToRest.Server.Hosting;

/// <summary>
/// Turns a database provider rejecting a client value (Npgsql refusing an offset-less DateTime for timestamptz, for
/// example) into a 400 instead of a 500. Only wraps execution, and only for exceptions the provider threw, so the
/// library's and EF Core's own errors still surface.
/// </summary>
internal static partial class ProviderRejections
{
    // Exceptions from these assemblies are bugs or misconfiguration, not a provider refusing a value.
    private static readonly HashSet<string> OwnAssemblies = new(StringComparer.Ordinal)
    {
        "LinqToRest.Server", "LinqToRest.Abstractions", "System.Linq.Expressions", "Microsoft.EntityFrameworkCore",
        "Microsoft.EntityFrameworkCore.Relational", "System.Private.CoreLib",
    };

    public static async Task<T> GuardAsync<T>(HttpContext context, string set, Func<Task<T>> execute)
    {
        try
        {
            return await execute().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRejection(exception))
        {
            LogRejected(QueryEndpoint.Logger(context), set, exception.GetType().Name, exception.Source);
            throw new RequestException(StatusCodes.Status400BadRequest, ErrorCodes.TypeMismatch, "");
        }
    }

    /// <summary>
    /// Whether the provider refused a parameter value: Npgsql throws <see cref="ArgumentException"/>, other providers
    /// <see cref="InvalidCastException"/>.
    /// </summary>
    public static bool IsRejection(Exception exception) =>
        exception is ArgumentException or InvalidCastException && exception.Source is { } source && !OwnAssemblies.Contains(source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database provider rejected a value of a LinqToRest request for '{Set}' ({Exception} from {Source}); answered 400 type_mismatch. Check the column types and value converters of that set.")]
    private static partial void LogRejected(ILogger logger, string set, string exception, string? source);
}

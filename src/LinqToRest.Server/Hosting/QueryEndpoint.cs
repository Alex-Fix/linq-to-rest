using System.Text.Json;
using LinqToRest.Protocol;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinqToRest.Server.Hosting;

internal static partial class QueryEndpoint
{
    public static async Task HandleAsync(HttpContext context, string set)
    {
        var server = context.RequestServices.GetRequiredService<ServerModel>();
        try
        {
            var mapping = server.FindSet(set) ?? throw new RequestException(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "");
            var body = await RequestBody.ReadAsync(context.Request, server.Limits.MaxBodyBytes).ConfigureAwait(false);
            var request = QueryJson.Read(body, server.Limits);
            var policies = new RequestPolicies(new PolicyContext(context));
            if (!await policies.CanAsync(mapping, DtoOperation.Read).ConfigureAwait(false))
            {
                throw new RequestException(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, "");
            }

            var plan = QueryCompiler.Compile(server, mapping, request, policies);
            var db = (DbContext)context.RequestServices.GetRequiredService(server.ContextType);
            var result = await ProviderRejections.GuardAsync(context, mapping.DtoType.SetName, () => mapping.ExecuteAsync(db, plan, context.RequestAborted)).ConfigureAwait(false);
            await WriteAsync(context.Response, plan.Shape, result).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ProtocolException or RequestException)
        {
            var error = exception as RequestException ?? RequestException.From((ProtocolException)exception);
            var logger = Logger(context);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                LogRejected(logger, error.Status, error.Code, error.Location);
            }

            await Problems.WriteAsync(context, error).ConfigureAwait(false);
        }
    }

    internal static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LinqToRest.Server");

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rejected LinqToRest request: {Status} {Code} at '{Location}'")]
    internal static partial void LogRejected(ILogger logger, int status, string code, string location);

    private static async Task WriteAsync(HttpResponse response, Shape shape, QueryResult result)
    {
        response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(response.BodyWriter);
        writer.WriteStartObject();
        writer.WriteStartArray("items");
        foreach (var row in result.Rows)
        {
            shape.Write(writer, row);
        }

        writer.WriteEndArray();
        if (result.NextCursor is not null)
        {
            writer.WriteString("nextCursor", result.NextCursor);
        }

        if (result.TotalCount is { } total)
        {
            writer.WriteNumber("totalCount", total);
        }

        writer.WriteEndObject();
        await writer.FlushAsync(response.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}

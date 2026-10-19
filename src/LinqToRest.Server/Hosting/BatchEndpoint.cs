using LinqToRest.Protocol;
using LinqToRest.Server.Batch;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LinqToRest.Server.Hosting;

internal static class BatchEndpoint
{
    public static async Task HandleAsync(HttpContext context)
    {
        var server = context.RequestServices.GetRequiredService<ServerModel>();
        try
        {
            var body = await RequestBody.ReadAsync(context.Request, server.Limits.MaxBatchBodyBytes).ConfigureAwait(false);
            var batch = BatchJson.Read(body, server.Limits);
            var db = (DbContext)context.RequestServices.GetRequiredService(server.ContextType);
            var policies = new RequestPolicies(new PolicyContext(context));
            var response = await ProviderRejections.GuardAsync(context, "$batch", () => new BatchExecutor(server, db, policies).ExecuteAsync(batch, context.RequestAborted)).ConfigureAwait(false);
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(BatchJson.WriteResponse(response), context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ProtocolException or RequestException)
        {
            var error = exception as RequestException ?? RequestException.From((ProtocolException)exception);
            var logger = QueryEndpoint.Logger(context);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                QueryEndpoint.LogRejected(logger, error.Status, error.Code, error.Location);
            }

            await Problems.WriteAsync(context, error).ConfigureAwait(false);
        }
    }
}

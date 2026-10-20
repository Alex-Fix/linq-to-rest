using System.Security.Cryptography;
using System.Text.Json;
using LinqToRest.Server.Mapping;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;

namespace LinqToRest.Server.Hosting;

/// <summary>Exports the model document clients are generated from.</summary>
public static class LinqToRestModelExtensions
{
    /// <summary>
    /// Builds and validates the sets and writes the model document to <paramref name="path"/>, for example from a
    /// <c>--export-linqtorest-model &lt;path&gt;</c> switch in <c>Program.cs</c>. It runs in the app's own host, so it
    /// sees the same services, EF Core model and configuration as the running API. Nothing is started or served.
    /// </summary>
    /// <param name="host">The built app.</param>
    /// <param name="path">The file to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is written.</returns>
    public static Task ExportLinqToRestModelAsync(this IHost host, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.WriteAllBytesAsync(path, Document(host.Services.GetRequiredService<ServerModel>()), cancellationToken);
    }

    /// <summary>
    /// Maps <c>GET {prefix}/$model</c>, which serves the model document (the same bytes the export writes) with an
    /// <c>ETag</c>. It reveals every set, member and type, so it's off unless mapped, and the route group's
    /// conventions (such as <c>RequireAuthorization()</c>) apply to it.
    /// </summary>
    /// <param name="group">The group <c>MapLinqToRest</c> returned.</param>
    /// <returns>The same group.</returns>
    public static RouteGroupBuilder WithModelEndpoint(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var document = Document(((IEndpointRouteBuilder)group).ServiceProvider.GetRequiredService<ServerModel>());
        var etag = new EntityTagHeaderValue($"\"{Convert.ToBase64String(SHA256.HashData(document)[..16])}\"");
        group.MapGet("$model", (HttpContext context) =>
            {
                context.Response.Headers.ETag = etag.ToString();
                if (context.Request.GetTypedHeaders().IfNoneMatch.Any(match => match.Compare(etag, useStrongComparison: false) || match.Equals(EntityTagHeaderValue.Any)))
                {
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                }

                return Results.Bytes(document, "application/json");
            })
            .WithName("LinqToRestModel")
            .WithTags("LinqToRest")
            .WithSummary("Get the model")
            .WithDescription("The model document clients are generated from: sets, members, types, keys, navigations and allowed writes.")
            .Produces<JsonElement>(StatusCodes.Status200OK, "application/json")
            .Produces(StatusCodes.Status304NotModified);
        return group;
    }

    private static byte[] Document(ServerModel server) => [.. server.Model.ToUtf8Json(), (byte)'\n'];
}

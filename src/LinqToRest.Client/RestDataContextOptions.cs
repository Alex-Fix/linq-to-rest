using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinqToRest.Client;

/// <summary>The model and HTTP client a <see cref="RestDataContext"/> works with.</summary>
public class RestDataContextOptions
{
    /// <summary>Creates the options.</summary>
    /// <param name="model">The model, bound to the client classes.</param>
    /// <param name="httpClient">The client; its base address is the API root and must end with <c>/</c>.</param>
    protected RestDataContextOptions(ClientModel model, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(httpClient);
        Model = model;
        HttpClient = httpClient;
    }

    /// <summary>Gets the model, bound to the client classes.</summary>
    public ClientModel Model { get; }

    /// <summary>Gets the HTTP client.</summary>
    public HttpClient HttpClient { get; }

    /// <summary>
    /// Gets JSON options matching the wire (camelCase, enums by name, no numbers as strings). The client no longer reads
    /// results with them: rows and projections are read through the model's bindings, without serializer metadata.
    /// </summary>
    [Obsolete("Results are read through the model's bindings; these options aren't used. Will be removed in the next breaking release.")]
    public JsonSerializerOptions SerializerOptions
    {
        [RequiresUnreferencedCode("Uses reflection-based serializer metadata.")]
        [RequiresDynamicCode("Uses reflection-based serializer metadata.")]
        get => _serializerOptions ??= CreateSerializerOptions();
    }

    private static JsonSerializerOptions? _serializerOptions;

    [RequiresUnreferencedCode("Uses reflection-based serializer metadata.")]
    [RequiresDynamicCode("Uses reflection-based serializer metadata.")]
    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Options for a specific context type, so each context resolves its own from DI.</summary>
/// <typeparam name="TContext">The context type.</typeparam>
public sealed class RestDataContextOptions<TContext> : RestDataContextOptions
    where TContext : RestDataContext
{
    /// <summary>Creates the options.</summary>
    /// <param name="model">The model, bound to the client classes.</param>
    /// <param name="httpClient">The client; its base address is the API root and must end with <c>/</c>.</param>
    public RestDataContextOptions(ClientModel model, HttpClient httpClient)
        : base(model, httpClient)
    {
    }
}

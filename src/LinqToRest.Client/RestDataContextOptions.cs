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
    /// Gets the JSON options used to read results (camelCase, enums by name, no numbers as strings). They're shared by
    /// every context and read-only, so serialization metadata is built once rather than per context.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; } = SharedSerializerOptions;

    private static readonly JsonSerializerOptions SharedSerializerOptions = CreateSerializerOptions();

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

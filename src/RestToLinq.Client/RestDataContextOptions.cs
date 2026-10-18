using System.Text.Json;
using System.Text.Json.Serialization;
using RestToLinq.Modeling;

namespace RestToLinq.Client;

/// <summary>The model and HTTP client a <see cref="RestDataContext"/> works with.</summary>
public class RestDataContextOptions
{
    /// <summary>Creates the options.</summary>
    /// <param name="model">The shared DTO model.</param>
    /// <param name="httpClient">The client; its base address is the API root and must end with <c>/</c>.</param>
    protected RestDataContextOptions(DtoModel model, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(httpClient);
        Model = model;
        HttpClient = httpClient;
    }

    /// <summary>Gets the shared DTO model.</summary>
    public DtoModel Model { get; }

    /// <summary>Gets the HTTP client.</summary>
    public HttpClient HttpClient { get; }

    /// <summary>Gets the JSON options used to read results (camelCase, enums by name, no numbers as strings).</summary>
    public JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}

/// <summary>Options for a specific context type, so each context resolves its own from DI.</summary>
/// <typeparam name="TContext">The context type.</typeparam>
public sealed class RestDataContextOptions<TContext> : RestDataContextOptions
    where TContext : RestDataContext
{
    /// <summary>Creates the options.</summary>
    /// <param name="model">The shared DTO model.</param>
    /// <param name="httpClient">The client; its base address is the API root and must end with <c>/</c>.</param>
    public RestDataContextOptions(DtoModel model, HttpClient httpClient)
        : base(model, httpClient)
    {
    }
}

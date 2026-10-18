using Microsoft.Extensions.DependencyInjection;
using RestToLinq.Modeling;

namespace RestToLinq.Client;

/// <summary>Registers RestToLinq data contexts.</summary>
public static class RestDataContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TContext"/> as a scoped service with its own named <see cref="HttpClient"/>.
    /// Returns the client builder, so handlers (authentication, resilience…) are configured the usual way.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="model">The shared DTO model.</param>
    /// <param name="configureClient">Configures the client; set its base address to the API root, ending with <c>/</c>.</param>
    /// <typeparam name="TContext">The context type; its constructor takes <see cref="RestDataContextOptions{TContext}"/>.</typeparam>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder AddRestDataContext<TContext>(this IServiceCollection services, DtoModel model, Action<HttpClient> configureClient)
        where TContext : RestDataContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(configureClient);
        var name = typeof(TContext).FullName!;
        var builder = services.AddHttpClient(name, configureClient);
        services.AddScoped(provider => new RestDataContextOptions<TContext>(model, provider.GetRequiredService<IHttpClientFactory>().CreateClient(name)));
        services.AddScoped<TContext>();
        return builder;
    }
}

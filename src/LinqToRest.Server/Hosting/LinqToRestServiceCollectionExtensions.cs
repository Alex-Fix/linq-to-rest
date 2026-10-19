using LinqToRest.Modeling;
using LinqToRest.Server.Mapping;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.Hosting;

/// <summary>Registers the LinqToRest server.</summary>
public static class LinqToRestServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LinqToRest server for <paramref name="model"/>. Mappings are validated immediately, so a bad
    /// mapping fails at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="model">The shared DTO model.</param>
    /// <param name="configure">Adds mappings, the DbContext, limits and policies.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddLinqToRest(this IServiceCollection services, DtoModel model, Action<LinqToRestServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new LinqToRestServerBuilder(services);
        configure(builder);
        services.AddSingleton(MappingRegistration.CreateModel(model, builder.Mappings, builder.ContextType, builder.QueryLimits));
        return services;
    }
}

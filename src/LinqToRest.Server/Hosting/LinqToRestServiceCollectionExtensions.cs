using LinqToRest.Server.Mapping;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.Hosting;

/// <summary>Registers the LinqToRest server.</summary>
public static class LinqToRestServiceCollectionExtensions
{
    /// <summary>
    /// Registers the LinqToRest server. The sets are validated against the EF Core model when the endpoints are
    /// mapped (<c>MapLinqToRest</c>), so a bad configuration fails at startup.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adds sets, the DbContext, limits and policies.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddLinqToRest(this IServiceCollection services, Action<LinqToRestServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new LinqToRestServerBuilder(services);
        configure(builder);
        if (builder.ContextType is null)
        {
            throw new InvalidOperationException("LinqToRest needs a DbContext. Call UseDbContext<TContext>().");
        }

        services.AddSingleton(provider => ServerModelBuilder.Build(provider, builder.Sets, builder.ValueTypes, builder.ContextType, builder.QueryLimits));
        return services;
    }
}

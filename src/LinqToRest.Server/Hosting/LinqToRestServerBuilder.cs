using System.Reflection;
using LinqToRest.Protocol;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Sets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.Hosting;

/// <summary>Configures the LinqToRest server: sets, value types, DbContext, limits and policies.</summary>
public sealed class LinqToRestServerBuilder
{
    internal LinqToRestServerBuilder(IServiceCollection services) => Services = services;

    /// <summary>Gets the service collection.</summary>
    public IServiceCollection Services { get; }

    internal List<SetRegistration> Sets { get; } = [];

    internal List<Func<ValueTypeDefinition>> ValueTypes { get; } = [];

    internal Type? ContextType { get; private set; }

    internal QueryLimits QueryLimits { get; } = new();

    /// <summary>Adds a set configured inline.</summary>
    /// <param name="configure">Configures the set.</param>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddSet<TEntity>(Action<EntitySetBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        Sets.Add(new SetRegistration<TEntity>(configure));
        return this;
    }

    /// <summary>Adds a set configuration.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddSet<TEntity>(IEntitySetConfiguration<TEntity> configuration)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddSet<TEntity>(configuration.Configure);
    }

    /// <summary>Adds a value type configured inline.</summary>
    /// <param name="configure">Configures the value type.</param>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddValueType<TValue>(Action<ValueTypeBuilder<TValue>> configure)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        ValueTypes.Add(() =>
        {
            var builder = new ValueTypeBuilder<TValue>();
            configure(builder);
            return builder.Definition;
        });
        return this;
    }

    /// <summary>Adds a value type configuration.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <typeparam name="TValue">The value type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddValueType<TValue>(IValueTypeConfiguration<TValue> configuration)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddValueType<TValue>(configuration.Configure);
    }

    /// <summary>
    /// Adds every non-abstract <see cref="IEntitySetConfiguration{TEntity}"/> and <see cref="IValueTypeConfiguration{TValue}"/>
    /// with a parameterless constructor in an assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddSetsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Scan(assembly, typeof(IEntitySetConfiguration<>), nameof(AddSet));
        Scan(assembly, typeof(IValueTypeConfiguration<>), nameof(AddValueType));
        return this;
    }

    private void Scan(Assembly assembly, Type configurationType, string addName)
    {
        var add = typeof(LinqToRestServerBuilder).GetMethods()
            .Single(method => method.Name == addName && method.GetParameters()[0].ParameterType.IsGenericType
                && method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == configurationType);
        var configurations = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false } && type.GetConstructor(Type.EmptyTypes) is not null)
            .SelectMany(type => type.GetInterfaces()
                .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == configurationType)
                .Select(contract => (Type: type, Configured: contract.GetGenericArguments()[0])))
            .OrderBy(configuration => configuration.Type.FullName, StringComparer.Ordinal);
        foreach (var (type, configured) in configurations)
        {
            add.MakeGenericMethod(configured).Invoke(this, [Activator.CreateInstance(type)]);
        }
    }

    /// <summary>Sets the DbContext queries and batches run against (resolved per request from DI).</summary>
    /// <typeparam name="TContext">The DbContext type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder UseDbContext<TContext>()
        where TContext : DbContext
    {
        ContextType = typeof(TContext);
        return this;
    }

    /// <summary>Changes the request limits (body size, depth, nodes, batch size…).</summary>
    /// <param name="configure">Changes the limits.</param>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder Limits(Action<QueryLimits> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(QueryLimits);
        return this;
    }

    /// <summary>Registers a <see cref="SetPolicy{TEntity}"/> as a scoped service.</summary>
    /// <typeparam name="TPolicy">The policy type.</typeparam>
    /// <returns>The same builder.</returns>
    public LinqToRestServerBuilder AddPolicy<TPolicy>()
        where TPolicy : class
    {
        var policyBase = typeof(TPolicy);
        while (policyBase is not null && !(policyBase.IsGenericType && policyBase.GetGenericTypeDefinition() == typeof(SetPolicy<>)))
        {
            policyBase = policyBase.BaseType;
        }

        Services.AddScoped(policyBase ?? throw new ArgumentException($"{typeof(TPolicy).Name} doesn't derive from SetPolicy<TEntity>.", nameof(TPolicy)), typeof(TPolicy));
        return this;
    }
}

using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace LinqToRest.Client.Tracking;

/// <summary>
/// Compiled getters for the properties, navigations and key of one entity type, built once per type: change detection
/// reads every member of every tracked entity, and <see cref="PropertyInfo.GetValue(object)"/> is reflection each time.
/// </summary>
internal sealed class Accessors
{
    private static readonly ConditionalWeakTable<ClientSet, Accessors> Cache = [];

    private Accessors(ClientSet type)
    {
        Properties = [.. type.Properties.Select(property => Getter(property.PropertyInfo))];
        Navigations = [.. type.Navigations.Select(navigation => Getter(navigation.PropertyInfo))];
        Key = [.. type.Key.Select(property => Getter(property.PropertyInfo))];
    }

    /// <summary>Gets a getter per <see cref="ClientSet.Properties"/> item, in the same order.</summary>
    public Func<object, object?>[] Properties { get; }

    /// <summary>Gets a getter per <see cref="ClientSet.Navigations"/> item, in the same order.</summary>
    public Func<object, object?>[] Navigations { get; }

    /// <summary>Gets a getter per <see cref="ClientSet.Key"/> item, in the same order.</summary>
    public Func<object, object?>[] Key { get; }

    public static Accessors For(ClientSet type) => Cache.GetValue(type, static type => new Accessors(type));

    private static Func<object, object?> Getter(PropertyInfo property)
    {
        var entity = Expression.Parameter(typeof(object), "entity");
        var value = Expression.Property(Expression.Convert(entity, property.DeclaringType!), property);
        return Expression.Lambda<Func<object, object?>>(Expression.Convert(value, typeof(object)), entity).Compile();
    }
}

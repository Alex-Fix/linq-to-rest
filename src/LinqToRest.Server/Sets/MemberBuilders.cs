using System.Linq.Expressions;
using LinqToRest.Server.Internal;

namespace LinqToRest.Server.Sets;

/// <summary>Configures an exposed entity property.</summary>
public sealed class EntityPropertyBuilder
{
    private readonly MemberDefinition _member;

    internal EntityPropertyBuilder(MemberDefinition member) => _member = member;

    /// <summary>Sets the wire name. Default: the camelCase property name.</summary>
    /// <param name="name">The wire name: a lowercase letter followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder HasName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _member.WireName = name;
        return this;
    }

    /// <summary>Keeps the property out of inserts and updates.</summary>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder IsReadOnly()
    {
        _member.IsReadOnly = true;
        return this;
    }

    /// <summary>Allows queries to sort by the property. Index it.</summary>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder IsSortable()
    {
        _member.IsSortable = true;
        return this;
    }

    /// <summary>Keeps the property out of filters (for example large text).</summary>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder IsNotFilterable()
    {
        _member.IsNotFilterable = true;
        return this;
    }

    /// <summary>
    /// Requires a value on inserts and updates: not null, and for strings and collections not empty or blank (code
    /// <c>required</c>). Exported to clients, which check it before sending.
    /// </summary>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder IsRequired()
    {
        _member.Rules.IsRequired = true;
        return this;
    }

    /// <summary>Sets the minimum length of a string, or item count of a collection (code <c>too_short</c>). Exported to clients.</summary>
    /// <param name="length">The minimum, inclusive.</param>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder HasMinLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _member.Rules.MinLength = length;
        return this;
    }

    /// <summary>Sets the maximum length of a string, or item count of a collection (code <c>too_long</c>). Exported to clients.</summary>
    /// <param name="length">The maximum, inclusive.</param>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder HasMaxLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _member.Rules.MaxLength = length;
        return this;
    }

    /// <summary>Sets the inclusive range of a number, date or time (code <c>out_of_range</c>). Exported to clients.</summary>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <typeparam name="T">The property's type (without <see cref="Nullable{T}"/>).</typeparam>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder HasRange<T>(T min, T max)
        where T : struct, IComparable<T> =>
        HasMin(min).HasMax(max);

    /// <summary>Sets the inclusive lower bound of a number, date or time (code <c>out_of_range</c>). Exported to clients.</summary>
    /// <param name="min">The lower bound.</param>
    /// <typeparam name="T">The property's type (without <see cref="Nullable{T}"/>).</typeparam>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder HasMin<T>(T min)
        where T : struct, IComparable<T>
    {
        _member.Rules.Min = min;
        return this;
    }

    /// <summary>Sets the inclusive upper bound of a number, date or time (code <c>out_of_range</c>). Exported to clients.</summary>
    /// <param name="max">The upper bound.</param>
    /// <typeparam name="T">The property's type (without <see cref="Nullable{T}"/>).</typeparam>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder HasMax<T>(T max)
        where T : struct, IComparable<T>
    {
        _member.Rules.Max = max;
        return this;
    }

    /// <summary>
    /// Sets the pattern a string must match (code <c>invalid_format</c>); anchor it (<c>^…$</c>) to match the whole value.
    /// The pattern must mean the same in .NET and JavaScript, where clients check it: no inline options, <c>\p{…}</c>,
    /// <c>\A</c>/<c>\z</c>, atomic groups or class subtraction. At most 500 characters.
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The same builder.</returns>
    public EntityPropertyBuilder HasPattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        _member.Rules.Pattern = pattern;
        return this;
    }
}

/// <summary>Configures an exposed computed value.</summary>
public sealed class ComputedPropertyBuilder
{
    private readonly MemberDefinition _member;

    internal ComputedPropertyBuilder(MemberDefinition member) => _member = member;

    /// <summary>
    /// Declares that a reference-type value (such as a string) is never null. Default: nullable, because the
    /// expression's nullability can't be inferred.
    /// </summary>
    /// <returns>The same builder.</returns>
    public ComputedPropertyBuilder IsRequired()
    {
        _member.IsRequired = true;
        return this;
    }

    /// <summary>Allows queries to sort by the value.</summary>
    /// <returns>The same builder.</returns>
    public ComputedPropertyBuilder IsSortable()
    {
        _member.IsSortable = true;
        return this;
    }

    /// <summary>Keeps the value out of filters.</summary>
    /// <returns>The same builder.</returns>
    public ComputedPropertyBuilder IsNotFilterable()
    {
        _member.IsNotFilterable = true;
        return this;
    }
}

/// <summary>Configures an exposed reference navigation.</summary>
/// <typeparam name="TEntity">The declaring entity type.</typeparam>
public sealed class ReferenceNavigationBuilder<TEntity>
    where TEntity : class
{
    private readonly NavigationDefinition _navigation;

    internal ReferenceNavigationBuilder(NavigationDefinition navigation) => _navigation = navigation;

    /// <summary>
    /// Names the exposed property holding the target's key, so clients can set the relationship. It must be the
    /// foreign key of the EF Core navigation.
    /// </summary>
    /// <param name="foreignKey">The foreign key property.</param>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <returns>The same builder.</returns>
    public ReferenceNavigationBuilder<TEntity> WithForeignKey<TKey>(Expression<Func<TEntity, TKey>> foreignKey)
    {
        ArgumentNullException.ThrowIfNull(foreignKey);
        _navigation.ForeignKey = MemberAccess.GetProperty(foreignKey, nameof(foreignKey));
        return this;
    }

    /// <summary>Sets the wire name. Default: the camelCase navigation name.</summary>
    /// <param name="name">The wire name: a lowercase letter followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public ReferenceNavigationBuilder<TEntity> HasName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _navigation.WireName = name;
        return this;
    }
}

/// <summary>Configures an exposed collection navigation.</summary>
public sealed class CollectionNavigationBuilder
{
    private readonly NavigationDefinition _navigation;

    internal CollectionNavigationBuilder(NavigationDefinition navigation) => _navigation = navigation;

    /// <summary>Sets the wire name. Default: the camelCase navigation name.</summary>
    /// <param name="name">The wire name: a lowercase letter followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public CollectionNavigationBuilder HasName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _navigation.WireName = name;
        return this;
    }
}

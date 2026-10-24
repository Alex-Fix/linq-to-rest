using System.Linq.Expressions;
using LinqToRest.Server.Internal;

namespace LinqToRest.Server.Sets;

/// <summary>
/// Configures how a value type is exposed wherever sets use it. Nothing is exposed unless configured here. A value
/// written by a batch replaces the old one as a whole, so every use that can be written must expose every member EF
/// Core maps.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
public sealed class ValueTypeBuilder<TValue>
    where TValue : class
{
    internal ValueTypeBuilder()
    {
    }

    internal ValueTypeDefinition Definition { get; } = new(typeof(TValue));

    /// <summary>Sets the name clients generate for the type. Default: the CLR type's name.</summary>
    /// <param name="name">The type name: an uppercase letter followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public ValueTypeBuilder<TValue> HasTypeName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Definition.TypeName = name;
        return this;
    }

    /// <summary>
    /// Exposes a member EF Core maps as a scalar property of the value type: <c>a =&gt; a.City</c>. Its wire name is the
    /// camelCase property name. It's filterable, and written with the value.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <returns>A builder for the member's options.</returns>
    public ValuePropertyBuilder Property<TProperty>(Expression<Func<TValue, TProperty>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new ValuePropertyBuilder(Definition.GetProperty(MemberAccess.GetProperty(property, nameof(property)), property));
    }

    /// <summary>
    /// Exposes a nested value type (configured on its own): <c>a =&gt; a.Geo</c>. Value types nest at most three levels.
    /// </summary>
    /// <param name="value">The nested complex or owned property.</param>
    /// <typeparam name="TNested">The nested value type.</typeparam>
    /// <returns>A builder for the member's options.</returns>
    public NestedValueBuilder Value<TNested>(Expression<Func<TValue, TNested?>> value)
        where TNested : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return new NestedValueBuilder(Definition.GetValue(MemberAccess.GetProperty(value, nameof(value)), value));
    }
}

/// <summary>Configures an exposed member of a value type.</summary>
public sealed class ValuePropertyBuilder
{
    private readonly MemberDefinition _member;

    internal ValuePropertyBuilder(MemberDefinition member) => _member = member;

    /// <summary>Sets the wire name. Default: the camelCase property name.</summary>
    /// <param name="name">The wire name: a lowercase letter followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder HasName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _member.WireName = name;
        return this;
    }

    /// <summary>Allows queries to sort by the member. Index it.</summary>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder IsSortable()
    {
        _member.IsSortable = true;
        return this;
    }

    /// <summary>Keeps the member out of filters (for example large text).</summary>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder IsNotFilterable()
    {
        _member.IsNotFilterable = true;
        return this;
    }

    /// <summary>Allows grouped queries to use the member as a key. Index it.</summary>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder IsGroupable()
    {
        _member.IsGroupable = true;
        return this;
    }

    /// <summary>Allows sum, min, max and avg over groups to use the member (numbers, dates and times).</summary>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder IsAggregatable()
    {
        _member.IsAggregatable = true;
        return this;
    }

    /// <summary>
    /// Requires a value whenever the value type is written: not null, and for strings not empty or blank (code
    /// <c>required</c>). Exported to clients, which check it before sending.
    /// </summary>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder IsRequired()
    {
        _member.Rules.IsRequired = true;
        return this;
    }

    /// <summary>Sets the minimum length of a string (code <c>too_short</c>). Exported to clients.</summary>
    /// <param name="length">The minimum, inclusive.</param>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder HasMinLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _member.Rules.MinLength = length;
        return this;
    }

    /// <summary>Sets the maximum length of a string (code <c>too_long</c>). Exported to clients.</summary>
    /// <param name="length">The maximum, inclusive.</param>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder HasMaxLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _member.Rules.MaxLength = length;
        return this;
    }

    /// <summary>Sets the inclusive range of a number, date or time (code <c>out_of_range</c>). Exported to clients.</summary>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <typeparam name="T">The member's type (without <see cref="Nullable{T}"/>).</typeparam>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder HasRange<T>(T min, T max)
        where T : struct, IComparable<T> =>
        HasMin(min).HasMax(max);

    /// <summary>Sets the inclusive lower bound of a number, date or time (code <c>out_of_range</c>). Exported to clients.</summary>
    /// <param name="min">The lower bound.</param>
    /// <typeparam name="T">The member's type (without <see cref="Nullable{T}"/>).</typeparam>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder HasMin<T>(T min)
        where T : struct, IComparable<T>
    {
        _member.Rules.Min = min;
        return this;
    }

    /// <summary>Sets the inclusive upper bound of a number, date or time (code <c>out_of_range</c>). Exported to clients.</summary>
    /// <param name="max">The upper bound.</param>
    /// <typeparam name="T">The member's type (without <see cref="Nullable{T}"/>).</typeparam>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder HasMax<T>(T max)
        where T : struct, IComparable<T>
    {
        _member.Rules.Max = max;
        return this;
    }

    /// <summary>
    /// Sets the pattern a string must match (code <c>invalid_format</c>); anchor it (<c>^…$</c>) to match the whole value.
    /// The same restrictions as on set properties apply (it must mean the same in .NET and JavaScript).
    /// </summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The same builder.</returns>
    public ValuePropertyBuilder HasPattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        _member.Rules.Pattern = pattern;
        return this;
    }
}

/// <summary>Configures a set property holding a value type.</summary>
public sealed class ValueUseBuilder
{
    private readonly MemberDefinition _member;

    internal ValueUseBuilder(MemberDefinition member) => _member = member;

    /// <summary>Sets the wire name. Default: the camelCase property name.</summary>
    /// <param name="name">The wire name: a lowercase letter followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public ValueUseBuilder HasName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _member.WireName = name;
        return this;
    }

    /// <summary>Keeps the value out of inserts and updates.</summary>
    /// <returns>The same builder.</returns>
    public ValueUseBuilder IsReadOnly()
    {
        _member.IsReadOnly = true;
        return this;
    }

    /// <summary>Keeps the value and its members out of filters.</summary>
    /// <returns>The same builder.</returns>
    public ValueUseBuilder IsNotFilterable()
    {
        _member.IsNotFilterable = true;
        return this;
    }
}

/// <summary>Configures a value type member holding another value type.</summary>
public sealed class NestedValueBuilder
{
    private readonly MemberDefinition _member;

    internal NestedValueBuilder(MemberDefinition member) => _member = member;

    /// <summary>Sets the wire name. Default: the camelCase property name.</summary>
    /// <param name="name">The wire name: a lowercase letter followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public NestedValueBuilder HasName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _member.WireName = name;
        return this;
    }

    /// <summary>Keeps the value and its members out of filters.</summary>
    /// <returns>The same builder.</returns>
    public NestedValueBuilder IsNotFilterable()
    {
        _member.IsNotFilterable = true;
        return this;
    }
}

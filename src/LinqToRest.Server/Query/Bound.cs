using System.Collections.Immutable;
using System.Linq.Expressions;
using LinqToRest.Server.Mapping;

namespace LinqToRest.Server.Query;

internal enum MemberUse
{
    Filter,
    Sort,
    Select,

    /// <summary>A key of a grouped query: the member must be groupable.</summary>
    Group,

    /// <summary>Inside sum, min, max or avg over a group: the member must be aggregatable.</summary>
    Aggregate,
}

/// <summary>What a node or member path resolved to.</summary>
internal abstract class Bound;

/// <summary>A scalar value.</summary>
internal sealed class ValueBound(Expression expression, bool isNullable) : Bound
{
    public Expression Expression { get; } = expression;

    public bool IsNullable { get; } = isNullable;
}

/// <summary>A row of a set: the root, a reference navigation or a collection element.</summary>
// When Guard is set, the row only counts as present if it holds (the policy filter of a navigation).
internal sealed class EntityBound(Expression expression, SetMapping mapping, Expression? guard, bool isNullable) : Bound
{
    public Expression Expression { get; } = expression;

    public SetMapping Mapping { get; } = mapping;

    public Expression? Guard { get; } = guard;

    public bool IsNullable { get; } = isNullable;

    public bool NeedsLifting => IsNullable || Guard is not null;
}

/// <summary>
/// A value of a value type (an EF complex or owned member), read through <see cref="Owner"/>. Its members are columns
/// of the owner's row; the whole value can only be selected or compared with null.
/// </summary>
internal sealed class ValueObjectBound(Expression expression, ServerValueType valueType, EntityBound owner, bool isNullable, bool isFilterable) : Bound
{
    public Expression Expression { get; } = expression;

    public ServerValueType ValueType { get; } = valueType;

    public EntityBound Owner { get; } = owner;

    /// <summary>Gets whether the value, or a value holding it, may be null.</summary>
    public bool IsNullable { get; } = isNullable;

    /// <summary>Gets whether every property on the path to the value is filterable.</summary>
    public bool IsFilterable { get; } = isFilterable;

    public bool NeedsLifting => IsNullable || Owner.NeedsLifting;
}

/// <summary>A collection navigation or primitive collection.</summary>
// Source is the sequence lambdas compose over; Value is the materialized collection to project (primitive collections only).
internal sealed class CollectionBound(Expression source, Type elementType, SetMapping? elementMapping, Expression? value) : Bound
{
    public Expression Source { get; } = source;

    public Type ElementType { get; } = elementType;

    public SetMapping? ElementMapping { get; } = elementMapping;

    public Expression? Value { get; } = value;
}

internal sealed record Scope(EntityBound Root, ImmutableDictionary<string, Bound> Variables)
{
    /// <summary>Gets the group of a grouped select: its keys and rows (<c>{"key": n}</c>, <c>{"group": {}}</c>).</summary>
    public GroupScope? Group { get; init; }

    /// <summary>Gets whether bare member paths are refused: a grouped query has no single row to read them from.</summary>
    public bool RootHidden { get; init; }

    /// <summary>Gets the select's fields by name, which member paths name in a grouped query's having and order.</summary>
    public IReadOnlyDictionary<string, ValueBound>? Fields { get; init; }

    /// <summary>Gets whether this is the body of an aggregate over a group (no nested collection lambdas).</summary>
    public bool InGroupAggregate { get; init; }

    public Scope With(string name, Bound bound) => this with { Variables = Variables.Add(name, bound) };
}

/// <summary>The group a grouped select reads: the grouping parameter, its keys and the set of its rows.</summary>
internal sealed record GroupScope(ParameterExpression Group, IReadOnlyList<ValueBound> Keys, SetMapping Mapping);

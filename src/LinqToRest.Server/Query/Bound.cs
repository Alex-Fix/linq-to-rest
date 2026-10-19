using System.Collections.Immutable;
using System.Linq.Expressions;
using LinqToRest.Server.Mapping;

namespace LinqToRest.Server.Query;

internal enum MemberUse
{
    Filter,
    Sort,
    Select,
}

/// <summary>What a node or member path resolved to.</summary>
internal abstract class Bound;

/// <summary>A scalar value.</summary>
internal sealed class ValueBound(Expression expression, bool isNullable) : Bound
{
    public Expression Expression { get; } = expression;

    public bool IsNullable { get; } = isNullable;
}

/// <summary>A DTO row: the root, a reference navigation or a collection element.</summary>
// When Guard is set, the row only counts as present if it holds (the policy filter of a navigation).
internal sealed class EntityBound(Expression expression, SetMapping mapping, Expression? guard, bool isNullable) : Bound
{
    public Expression Expression { get; } = expression;

    public SetMapping Mapping { get; } = mapping;

    public Expression? Guard { get; } = guard;

    public bool IsNullable { get; } = isNullable;

    public bool NeedsLifting => IsNullable || Guard is not null;
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
    public Scope With(string name, Bound bound) => this with { Variables = Variables.Add(name, bound) };
}

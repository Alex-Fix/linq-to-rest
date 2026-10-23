using System.Linq.Expressions;
using LinqToRest.Protocol;

namespace LinqToRest.Server.Query;

/// <summary>A query bound to one set, ready to run.</summary>
internal sealed class QueryPlan
{
    public LambdaExpression? PolicyFilter { get; init; }

    public LambdaExpression? Filter { get; init; }

    public required IReadOnlyList<SortKey> Sorts { get; init; }

    public LambdaExpression? Keyset { get; init; }

    public required LambdaExpression Projection { get; init; }

    public required Shape Shape { get; init; }

    public PageMode Mode { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; }

    public bool Count { get; init; }

    public string? SortHash { get; init; }

    public IReadOnlyList<int> CursorIndexes { get; init; } = [];

    /// <summary>Gets the grouping of a grouped query; <see cref="Projection"/> then projects each group.</summary>
    public GroupPlan? Group { get; init; }
}

/// <summary>
/// A grouped query: the key selector over rows, then over groups the having filter and the order (keys last). Capped
/// queries have no take of their own: they read one group more than the limit to know whether it was exceeded.
/// </summary>
internal sealed record GroupPlan(LambdaExpression Key, LambdaExpression? Having, IReadOnlyList<SortKey> Sorts, int? MaxGroups);

internal sealed record SortKey(LambdaExpression Key, bool Descending, string Path);

internal sealed record QueryResult(IReadOnlyList<object?[]> Rows, string? NextCursor, long? TotalCount);

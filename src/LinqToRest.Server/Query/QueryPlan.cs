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
}

internal sealed record SortKey(LambdaExpression Key, bool Descending, string Path);

internal sealed record QueryResult(IReadOnlyList<object?[]> Rows, string? NextCursor, long? TotalCount);

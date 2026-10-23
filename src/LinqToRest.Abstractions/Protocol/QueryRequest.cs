namespace LinqToRest.Protocol;

/// <summary>The pagination mode of a query.</summary>
public enum PageMode
{
    /// <summary>Skip and take.</summary>
    Offset,

    /// <summary>Keyset pagination after a cursor.</summary>
    Cursor,
}

/// <summary>A query sent with HTTP QUERY to a set.</summary>
public sealed class QueryRequest
{
    /// <summary>Gets the filter predicate.</summary>
    public QueryNode? Filter { get; init; }

    /// <summary>Gets the sort members, in order.</summary>
    public IReadOnlyList<OrderByItem> OrderBy { get; init; } = [];

    /// <summary>Gets the projection; <see langword="null"/> returns the full row.</summary>
    public QueryNode? Select { get; init; }

    /// <summary>Gets the navigation paths to load with the full row.</summary>
    public IReadOnlyList<MemberNode> Include { get; init; } = [];

    /// <summary>Gets the pagination; <see langword="null"/> means offset mode with the default page size.</summary>
    public PageRequest? Page { get; init; }

    /// <summary>Gets whether to return the total count of matching rows (of groups, for a grouped query).</summary>
    public bool Count { get; init; }

    /// <summary>
    /// Gets the group keys: <see langword="null"/> for a query of rows, empty for one group of all rows (whole-query
    /// aggregates). A grouped query returns its <see cref="Select"/> per group.
    /// </summary>
    public IReadOnlyList<MemberNode>? GroupBy { get; init; }

    /// <summary>Gets the filter on groups; its member paths name the fields of <see cref="Select"/>.</summary>
    public QueryNode? Having { get; init; }
}

/// <summary>One sort member of a query.</summary>
/// <param name="Member">The member path.</param>
/// <param name="Descending">Whether to sort descending.</param>
public sealed record OrderByItem(MemberNode Member, bool Descending);

/// <summary>The pagination of a query.</summary>
public sealed class PageRequest
{
    /// <summary>Gets the pagination mode.</summary>
    public PageMode Mode { get; init; }

    /// <summary>Gets the rows to skip (offset mode).</summary>
    public int? Skip { get; init; }

    /// <summary>Gets the page size; <see langword="null"/> uses the set's default.</summary>
    public int? Take { get; init; }

    /// <summary>Gets the cursor to continue after (cursor mode).</summary>
    public string? After { get; init; }

    /// <summary>Gets the JSON pointer of the page object in the request it was read from.</summary>
    public string Location { get; init; } = "/page";
}

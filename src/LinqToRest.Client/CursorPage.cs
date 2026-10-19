namespace LinqToRest.Client;

/// <summary>One page of keyset pagination.</summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class CursorPage<T>
{
    internal CursorPage(IReadOnlyList<T> items, string? nextCursor, long? totalCount)
    {
        Items = items;
        NextCursor = nextCursor;
        TotalCount = totalCount;
    }

    /// <summary>Gets the items of the page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Gets the cursor of the next page, or <see langword="null"/> on the last page. Pass it to <c>After</c>.</summary>
    public string? NextCursor { get; }

    /// <summary>Gets the total count of matching rows, when requested.</summary>
    public long? TotalCount { get; }
}

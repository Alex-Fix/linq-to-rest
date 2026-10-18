namespace RestToLinq.Modeling;

/// <summary>The pagination settings of a set.</summary>
public sealed class DtoPagination
{
    internal DtoPagination(PaginationModes modes, int defaultPageSize, int maxPageSize, int maxSkip)
    {
        Modes = modes;
        DefaultPageSize = defaultPageSize;
        MaxPageSize = maxPageSize;
        MaxSkip = maxSkip;
    }

    /// <summary>Gets the accepted pagination modes.</summary>
    public PaginationModes Modes { get; }

    /// <summary>Gets the page size used when a query has no take.</summary>
    public int DefaultPageSize { get; }

    /// <summary>Gets the largest take a client may request.</summary>
    public int MaxPageSize { get; }

    /// <summary>Gets the largest skip a client may request in offset mode.</summary>
    public int MaxSkip { get; }
}

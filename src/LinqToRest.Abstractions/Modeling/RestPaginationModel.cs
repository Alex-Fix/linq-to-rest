namespace LinqToRest.Modeling;

/// <summary>The pagination rules of a <see cref="RestSetModel"/>.</summary>
public sealed class RestPaginationModel
{
    /// <summary>Gets the allowed modes. Default: offset and cursor.</summary>
    public PaginationModes Modes { get; init; } = PaginationModes.All;

    /// <summary>Gets the page size used when a query has no <c>take</c>. Default: 50.</summary>
    public int DefaultPageSize { get; init; } = 50;

    /// <summary>Gets the largest allowed <c>take</c>. Default: 200.</summary>
    public int MaxPageSize { get; init; } = 200;

    /// <summary>Gets the largest allowed <c>skip</c> in offset mode. Default: 10 000.</summary>
    public int MaxSkip { get; init; } = 10_000;
}

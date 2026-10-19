namespace LinqToRest.Modeling;

/// <summary>Configures the pagination of a set.</summary>
public sealed class DtoPaginationBuilder
{
    private readonly PaginationDefinition _definition;

    internal DtoPaginationBuilder(PaginationDefinition definition) => _definition = definition;

    /// <summary>Sets the accepted pagination modes. Default: <see cref="PaginationModes.All"/>.</summary>
    /// <param name="modes">The accepted modes.</param>
    /// <returns>The same builder.</returns>
    public DtoPaginationBuilder Modes(PaginationModes modes)
    {
        _definition.Modes = modes;
        return this;
    }

    /// <summary>Sets the page size used when a query has no take. Default: 50.</summary>
    /// <param name="size">The default page size.</param>
    /// <returns>The same builder.</returns>
    public DtoPaginationBuilder DefaultPageSize(int size)
    {
        _definition.DefaultPageSize = size;
        return this;
    }

    /// <summary>Sets the largest take a client may request. Default: 200.</summary>
    /// <param name="size">The maximum page size.</param>
    /// <returns>The same builder.</returns>
    public DtoPaginationBuilder MaxPageSize(int size)
    {
        _definition.MaxPageSize = size;
        return this;
    }

    /// <summary>Sets the largest skip a client may request in offset mode. Default: 10 000.</summary>
    /// <param name="skip">The maximum skip.</param>
    /// <returns>The same builder.</returns>
    public DtoPaginationBuilder MaxSkip(int skip)
    {
        _definition.MaxSkip = skip;
        return this;
    }
}

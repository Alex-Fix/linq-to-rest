using LinqToRest.Modeling;

namespace LinqToRest.Server.Sets;

/// <summary>Configures the pagination of a set.</summary>
public sealed class SetPaginationBuilder
{
    internal SetPaginationBuilder()
    {
    }

    internal RestPaginationModel Model { get; private set; } = new();

    /// <summary>Sets the accepted pagination modes. Default: <see cref="PaginationModes.All"/>.</summary>
    /// <param name="modes">The accepted modes.</param>
    /// <returns>The same builder.</returns>
    public SetPaginationBuilder Modes(PaginationModes modes)
    {
        Model = new RestPaginationModel { Modes = modes, DefaultPageSize = Model.DefaultPageSize, MaxPageSize = Model.MaxPageSize, MaxSkip = Model.MaxSkip };
        return this;
    }

    /// <summary>Sets the page size used when a query has no take. Default: 50.</summary>
    /// <param name="size">The default page size.</param>
    /// <returns>The same builder.</returns>
    public SetPaginationBuilder DefaultPageSize(int size)
    {
        Model = new RestPaginationModel { Modes = Model.Modes, DefaultPageSize = size, MaxPageSize = Model.MaxPageSize, MaxSkip = Model.MaxSkip };
        return this;
    }

    /// <summary>Sets the largest take a client may request. Default: 200.</summary>
    /// <param name="size">The maximum page size.</param>
    /// <returns>The same builder.</returns>
    public SetPaginationBuilder MaxPageSize(int size)
    {
        Model = new RestPaginationModel { Modes = Model.Modes, DefaultPageSize = Model.DefaultPageSize, MaxPageSize = size, MaxSkip = Model.MaxSkip };
        return this;
    }

    /// <summary>Sets the largest skip a client may request in offset mode. Default: 10 000.</summary>
    /// <param name="skip">The maximum skip.</param>
    /// <returns>The same builder.</returns>
    public SetPaginationBuilder MaxSkip(int skip)
    {
        Model = new RestPaginationModel { Modes = Model.Modes, DefaultPageSize = Model.DefaultPageSize, MaxPageSize = Model.MaxPageSize, MaxSkip = skip };
        return this;
    }
}

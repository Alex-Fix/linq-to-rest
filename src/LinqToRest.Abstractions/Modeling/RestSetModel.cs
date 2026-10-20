namespace LinqToRest.Modeling;

/// <summary>A set in a <see cref="RestModel"/>: its wire name, generated type name, members and rules.</summary>
public sealed class RestSetModel
{
    private Dictionary<string, RestPropertyModel> _propertiesByName = [];
    private Dictionary<string, RestNavigationModel> _navigationsByName = [];

    /// <summary>Gets the set name used in URLs and batch operations, for example <c>products</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the name of the type clients generate for the set's rows, for example <c>Product</c>.</summary>
    public required string TypeName { get; init; }

    /// <summary>Gets the names of the key properties, in order.</summary>
    public required IReadOnlyList<string> Key
    {
        get;
        init => field = [.. value];
    } = [];

    /// <summary>Gets the name of the concurrency token property, if the set has one.</summary>
    public string? ConcurrencyToken { get; init; }

    /// <summary>Gets the write operations the set allows. Default: none (read-only).</summary>
    public WriteOperations Writes { get; init; }

    /// <summary>Gets the pagination rules.</summary>
    public required RestPaginationModel Pagination { get; init; }

    /// <summary>Gets the scalar and primitive-collection properties.</summary>
    public required IReadOnlyList<RestPropertyModel> Properties
    {
        get;
        init => field = [.. value];
    } = [];

    /// <summary>Gets the navigations to other sets.</summary>
    public IReadOnlyList<RestNavigationModel> Navigations
    {
        get;
        init => field = [.. value];
    } = [];

    /// <summary>Finds a property by name.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The property, or <see langword="null"/>.</returns>
    public RestPropertyModel? FindProperty(string name) => _propertiesByName.GetValueOrDefault(name);

    /// <summary>Finds a navigation by name.</summary>
    /// <param name="name">The navigation name.</param>
    /// <returns>The navigation, or <see langword="null"/>.</returns>
    public RestNavigationModel? FindNavigation(string name) => _navigationsByName.GetValueOrDefault(name);

    /// <summary>Returns whether the set allows every operation in <paramref name="operations"/>.</summary>
    /// <param name="operations">The operations to check.</param>
    /// <returns><see langword="true"/> when all are allowed.</returns>
    public bool Allows(WriteOperations operations) => (Writes & operations) == operations;

    /// <inheritdoc/>
    public override string ToString() => Name;

    internal void Index()
    {
        _propertiesByName = Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
        _navigationsByName = Navigations.ToDictionary(navigation => navigation.Name, StringComparer.Ordinal);
    }
}

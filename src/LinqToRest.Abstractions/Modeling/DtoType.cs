namespace LinqToRest.Modeling;

/// <summary>A DTO type of the model and the set that exposes it.</summary>
public sealed class DtoType
{
    private readonly Dictionary<string, DtoProperty> _properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DtoNavigation> _navigations = new(StringComparer.Ordinal);

    internal DtoType(Type clrType, string setName, DtoPagination pagination)
    {
        ClrType = clrType;
        SetName = setName;
        Pagination = pagination;
    }

    /// <summary>Gets the DTO CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>Gets the set name used in request paths.</summary>
    public string SetName { get; }

    /// <summary>Gets the scalar and primitive-collection properties, in declaration order.</summary>
    public IReadOnlyList<DtoProperty> Properties { get; private set; } = [];

    /// <summary>Gets the navigations to other DTO types, in declaration order.</summary>
    public IReadOnlyList<DtoNavigation> Navigations { get; private set; } = [];

    /// <summary>Gets the key properties.</summary>
    public IReadOnlyList<DtoProperty> Key { get; private set; } = [];

    /// <summary>Gets the concurrency token property, if any.</summary>
    public DtoProperty? ConcurrencyToken { get; private set; }

    /// <summary>Gets the pagination settings of the set.</summary>
    public DtoPagination Pagination { get; }

    /// <summary>Finds a property by its wire (camelCase) name.</summary>
    /// <param name="wireName">The wire name.</param>
    /// <returns>The property, or <see langword="null"/>.</returns>
    public DtoProperty? FindProperty(string wireName) => _properties.GetValueOrDefault(wireName);

    /// <summary>Finds a navigation by its wire (camelCase) name.</summary>
    /// <param name="wireName">The wire name.</param>
    /// <returns>The navigation, or <see langword="null"/>.</returns>
    public DtoNavigation? FindNavigation(string wireName) => _navigations.GetValueOrDefault(wireName);

    internal void Initialize(
        IReadOnlyList<DtoProperty> properties,
        IReadOnlyList<DtoNavigation> navigations,
        IReadOnlyList<DtoProperty> key,
        DtoProperty? concurrencyToken)
    {
        Properties = properties;
        Navigations = navigations;
        Key = key;
        ConcurrencyToken = concurrencyToken;
        foreach (var property in properties)
        {
            _properties.Add(property.WireName, property);
        }

        foreach (var navigation in navigations)
        {
            _navigations.Add(navigation.WireName, navigation);
        }
    }

    /// <inheritdoc />
    public override string ToString() => ClrType.Name;
}

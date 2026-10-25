namespace LinqToRest.Modeling;

/// <summary>
/// A value type in a <see cref="RestModel"/>: a structured value without its own set (an address, dimensions), used by
/// properties of sets or of other value types and replaced as a whole when written.
/// </summary>
public sealed class RestValueTypeModel
{
    private Dictionary<string, RestPropertyModel> _propertiesByName = [];

    /// <summary>Gets the type name clients generate, for example <c>Address</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the exposed members. They carry no <see cref="RestPropertyModel.IsWritable"/> flag: whether a value can be
    /// written depends on the property that uses the value type.
    /// </summary>
    public required IReadOnlyList<RestPropertyModel> Properties
    {
        get;
        init => field = [.. value];
    } = [];

    /// <summary>Finds a member by name.</summary>
    /// <param name="name">The member name.</param>
    /// <returns>The member, or <see langword="null"/>.</returns>
    public RestPropertyModel? FindProperty(string name) => _propertiesByName.GetValueOrDefault(name);

    /// <inheritdoc/>
    public override string ToString() => Name;

    internal void Index() => _propertiesByName = Properties.ToDictionary(property => property.Name, StringComparer.Ordinal);
}

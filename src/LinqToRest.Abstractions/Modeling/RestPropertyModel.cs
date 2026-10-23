namespace LinqToRest.Modeling;

/// <summary>A scalar or primitive-collection property of a <see cref="RestSetModel"/>.</summary>
public sealed class RestPropertyModel
{
    /// <summary>Gets the wire name, for example <c>categoryName</c>.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the type: a name from <see cref="ScalarTypes.GetName"/> (for example <c>int32</c>) or the name of an enum
    /// in <see cref="RestModel.Enums"/>. For a collection, the element type.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>Gets whether the value (or, for a collection, the collection itself) may be null.</summary>
    public bool IsNullable { get; init; }

    /// <summary>Gets whether the property is a collection of <see cref="Type"/>.</summary>
    public bool IsCollection { get; init; }

    /// <summary>Gets whether inserts and updates may set the property.</summary>
    public bool IsWritable { get; init; }

    /// <summary>Gets whether queries may sort by the property. Keys are always sortable.</summary>
    public bool IsSortable { get; init; }

    /// <summary>Gets whether queries may filter on the property. Default: <see langword="true"/>.</summary>
    public bool IsFilterable { get; init; } = true;

    /// <summary>Gets whether grouped queries may use the property as a key.</summary>
    public bool IsGroupable { get; init; }

    /// <summary>Gets whether sum, min, max and avg over a group may use the property (numbers, dates and times).</summary>
    public bool IsAggregatable { get; init; }

    /// <summary>Gets the built-in validation rules of a writable property, or <see langword="null"/> for none.</summary>
    public RestPropertyRules? Rules { get; init; }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

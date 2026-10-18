using System.Reflection;

namespace RestToLinq.Modeling;

/// <summary>A scalar or primitive-collection property of a DTO type.</summary>
public sealed class DtoProperty
{
    internal DtoProperty(DtoType declaringType, PropertyInfo propertyInfo, string wireName, Type? elementType, bool isNullable)
    {
        DeclaringType = declaringType;
        PropertyInfo = propertyInfo;
        WireName = wireName;
        ElementType = elementType;
        IsNullable = isNullable;
    }

    /// <summary>Gets the declaring DTO type.</summary>
    public DtoType DeclaringType { get; }

    /// <summary>Gets the CLR property.</summary>
    public PropertyInfo PropertyInfo { get; }

    /// <summary>Gets the CLR property name.</summary>
    public string Name => PropertyInfo.Name;

    /// <summary>Gets the camelCase name used on the wire.</summary>
    public string WireName { get; }

    /// <summary>Gets the property type.</summary>
    public Type ClrType => PropertyInfo.PropertyType;

    /// <summary>Gets the element type for primitive collections, otherwise <see langword="null"/>.</summary>
    public Type? ElementType { get; }

    /// <summary>Gets whether the property is a collection of scalars.</summary>
    public bool IsCollection => ElementType is not null;

    /// <summary>Gets whether the property accepts <see langword="null"/>.</summary>
    public bool IsNullable { get; }

    /// <summary>Gets whether the property is part of the key.</summary>
    public bool IsKey { get; internal set; }

    /// <summary>Gets whether the property is the concurrency token.</summary>
    public bool IsConcurrencyToken { get; internal set; }

    /// <summary>Gets whether the property was configured as read-only.</summary>
    public bool IsReadOnly { get; internal set; }

    /// <summary>Gets whether clients may write the property (not key, token or read-only).</summary>
    public bool IsWritable => !IsKey && !IsConcurrencyToken && !IsReadOnly;

    /// <summary>Gets whether clients may sort by the property.</summary>
    public bool IsSortable { get; internal set; }

    /// <summary>Gets whether clients may filter by the property.</summary>
    public bool IsFilterable { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"{DeclaringType}.{Name}";
}

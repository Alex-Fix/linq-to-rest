using System.Reflection;

namespace LinqToRest.Modeling;

/// <summary>A navigation from one DTO type to another.</summary>
public sealed class DtoNavigation
{
    internal DtoNavigation(DtoType declaringType, PropertyInfo propertyInfo, string wireName, DtoType target, bool isCollection, bool isNullable)
    {
        DeclaringType = declaringType;
        PropertyInfo = propertyInfo;
        WireName = wireName;
        Target = target;
        IsCollection = isCollection;
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

    /// <summary>Gets the target DTO type.</summary>
    public DtoType Target { get; }

    /// <summary>Gets whether this is a collection navigation.</summary>
    public bool IsCollection { get; }

    /// <summary>Gets whether a reference navigation may be <see langword="null"/>.</summary>
    public bool IsNullable { get; }

    /// <summary>Gets the foreign key property of the declaring type for a reference navigation, if configured.</summary>
    public DtoProperty? ForeignKey { get; internal set; }

    /// <inheritdoc />
    public override string ToString() => $"{DeclaringType}.{Name}";
}

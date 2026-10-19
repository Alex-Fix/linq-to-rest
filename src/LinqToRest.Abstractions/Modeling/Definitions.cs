using System.Reflection;

namespace LinqToRest.Modeling;

// Mutable configuration collected by the builders; DtoModelBuilder.Build turns it into the immutable model.
internal sealed class DtoTypeDefinition(Type clrType)
{
    public Type ClrType { get; } = clrType;
    public string? SetName { get; set; }
    public IReadOnlyList<PropertyInfo>? Key { get; set; }
    public PropertyInfo? ConcurrencyToken { get; set; }
    public Dictionary<PropertyInfo, PropertyDefinition> Properties { get; } = [];
    public HashSet<PropertyInfo> Ignored { get; } = [];
    public Dictionary<PropertyInfo, NavigationDefinition> Navigations { get; } = [];
    public PaginationDefinition Pagination { get; } = new();

    public PropertyDefinition Property(PropertyInfo property)
    {
        if (!Properties.TryGetValue(property, out var definition))
        {
            definition = new PropertyDefinition();
            Properties.Add(property, definition);
        }

        return definition;
    }

    public NavigationDefinition Navigation(PropertyInfo property, Type target, bool isCollection)
    {
        if (!Navigations.TryGetValue(property, out var definition))
        {
            definition = new NavigationDefinition(target, isCollection);
            Navigations.Add(property, definition);
        }

        return definition;
    }
}

internal sealed class PropertyDefinition
{
    public bool IsReadOnly { get; set; }
    public bool IsSortable { get; set; }
    public bool IsNotFilterable { get; set; }
}

internal sealed class NavigationDefinition(Type target, bool isCollection)
{
    public Type Target { get; } = target;
    public bool IsCollection { get; } = isCollection;
    public PropertyInfo? ForeignKey { get; set; }
}

internal sealed class PaginationDefinition
{
    public PaginationModes Modes { get; set; } = PaginationModes.All;
    public int DefaultPageSize { get; set; } = 50;
    public int MaxPageSize { get; set; } = 200;
    public int MaxSkip { get; set; } = 10_000;
}

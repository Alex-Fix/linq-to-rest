using System.Linq.Expressions;
using System.Reflection;
using LinqToRest.Modeling;

namespace LinqToRest.Server.Sets;

/// <summary>What an <see cref="EntitySetBuilder{TEntity}"/> collected, validated when the server model is built.</summary>
internal sealed class SetDefinition(Type entityType)
{
    public Type EntityType { get; } = entityType;

    public string? Name { get; set; }

    public string? TypeName { get; set; }

    public LambdaExpression? Source { get; set; }

    public Type? SourceContextType { get; set; }

    public IReadOnlyList<PropertyInfo>? Key { get; set; }

    public MemberDefinition? ConcurrencyToken { get; set; }

    /// <summary>Gets the exposed scalar members, in the order they were first configured.</summary>
    public List<MemberDefinition> Members { get; } = [];

    public List<NavigationDefinition> Navigations { get; } = [];

    public WriteOperations Writes { get; set; }

    public SetPaginationBuilder Pagination { get; } = new();

    /// <summary>Gets the custom rules (<c>Must</c>), in the order they were added.</summary>
    public List<RuleDefinition.Custom> Rules { get; } = [];

    public MemberDefinition GetProperty(PropertyInfo property, LambdaExpression expression)
    {
        if (Members.Find(member => member.Property == property) is { } existing)
        {
            return existing;
        }

        var created = new MemberDefinition(property, null, expression);
        Members.Add(created);
        return created;
    }
}

/// <summary>A configured property (<see cref="Property"/> set) or computed value (<see cref="ComputedName"/> set).</summary>
internal sealed class MemberDefinition(PropertyInfo? property, string? computedName, LambdaExpression expression)
{
    public PropertyInfo? Property { get; } = property;

    public string? ComputedName { get; } = computedName;

    public LambdaExpression Expression { get; } = expression;

    public string? WireName { get; set; }

    public bool IsReadOnly { get; set; }

    public bool IsSortable { get; set; }

    public bool IsNotFilterable { get; set; }

    public bool IsRequired { get; set; }

    /// <summary>Gets the built-in validation rules (<see cref="EntityPropertyBuilder"/>).</summary>
    public RuleDefinition Rules { get; } = new();

    public string Describe() => Property?.Name ?? ComputedName!;
}

/// <summary>The built-in validation rules configured on a property; exported as <see cref="RestPropertyRules"/>.</summary>
internal sealed class RuleDefinition
{
    public bool IsRequired { get; set; }

    public int? MinLength { get; set; }

    public int? MaxLength { get; set; }

    public object? Min { get; set; }

    public object? Max { get; set; }

    public string? Pattern { get; set; }

    public RestPropertyRules? ToModel()
    {
        var rules = new RestPropertyRules { IsRequired = IsRequired, MinLength = MinLength, MaxLength = MaxLength, Min = Min, Max = Max, Pattern = Pattern };
        return rules.IsEmpty ? null : rules;
    }

    /// <summary>A <c>Must</c> rule: <paramref name="Member"/> is the property it's about, or null for the row.</summary>
    public sealed record Custom(PropertyInfo? Member, Func<object, bool> IsValid, string Code, bool OnlyWhenSent);
}

internal sealed class NavigationDefinition(PropertyInfo property, LambdaExpression expression, Type targetEntity, bool isCollection)
{
    public PropertyInfo Property { get; } = property;

    public LambdaExpression Expression { get; } = expression;

    public Type TargetEntity { get; } = targetEntity;

    public bool IsCollection { get; } = isCollection;

    public string? WireName { get; set; }

    public PropertyInfo? ForeignKey { get; set; }
}

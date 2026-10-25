using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Server.Sets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.Mapping;

/// <summary>
/// Validates the set configurations against the EF Core model and builds the <see cref="ServerModel"/> and its
/// <see cref="RestModel"/>. Runs once at startup; any problem throws with a message naming the set and member.
/// </summary>
internal static class ServerModelBuilder
{
    public static ServerModel Build(
        IServiceProvider services, IReadOnlyList<SetRegistration> registrations, IReadOnlyList<Func<ValueTypeDefinition>> valueTypes, Type? contextType, Protocol.QueryLimits limits)
    {
        if (contextType is null)
        {
            throw new InvalidOperationException("LinqToRest needs a DbContext. Call UseDbContext<TContext>().");
        }

        using var scope = services.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
        return Build(registrations, valueTypes, contextType, db.Model, limits);
    }

    public static ServerModel Build(
        IReadOnlyList<SetRegistration> registrations, IReadOnlyList<Func<ValueTypeDefinition>> valueTypes, Type contextType, IModel efModel, Protocol.QueryLimits limits)
    {
        var context = new ResolveContext();
        foreach (var definition in valueTypes.Select(configure => configure()))
        {
            if (!context.ValueDefinitions.TryAdd(definition.ClrType, definition))
            {
                throw InvalidValue(definition.ClrType.Name, "is configured twice");
            }
        }

        var definitions = registrations.Select(registration => (Registration: registration, Definition: registration.Configure())).ToList();
        var resolved = definitions
            .Select(item => (item.Registration, Set: Resolve(item.Definition, efModel, contextType, context)))
            .ToList();

        foreach (var (_, set) in resolved)
        {
            ResolveNavigations(set, [.. resolved.Select(item => item.Set)]);
        }

        var models = resolved.ToDictionary(item => item.Set, item => ToModel(item.Set));
        RestModel model;
        try
        {
            model = new RestModel(
                models.Values.OrderBy(set => set.Name, StringComparer.Ordinal),
                context.Enums.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new RestEnumModel { Name = pair.Key, Members = Enum.GetNames(pair.Value) }),
                context.ValueTypes.Values.OrderBy(valueType => valueType.Name, StringComparer.Ordinal).Select(ToModel));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"The LinqToRest model is invalid: {exception.Message}", exception);
        }

        // Value types first, so set and value members can point at them.
        var valueTypeMappings = context.ValueTypes.Values.ToDictionary(valueType => valueType, valueType => new ServerValueType(model.FindValueType(valueType.Name)!, valueType.Definition.ClrType));
        foreach (var (valueType, mapping) in valueTypeMappings)
        {
            var members = valueType.Properties.ToDictionary(property => property, property => property.ToSetProperty(valueTypeMappings));
            mapping.Initialize(
                [.. valueType.Properties.Select(property => members[property])],
                valueType.Construction is { } construction
                    ? new ValueConstruction(construction.Constructor, [.. construction.Arguments.Select(property => members[property])], [.. construction.Setters.Select(property => members[property])])
                    : null);
        }

        var mappings = resolved.ToDictionary(item => item.Set, item => item.Registration.Create(models[item.Set], item.Set.Definition, contextType));
        foreach (var (set, mapping) in mappings)
        {
            var properties = set.Properties.ToDictionary(property => property, property => property.ToSetProperty(valueTypeMappings));
            mapping.Initialize(
                [.. set.Properties.Select(property => properties[property])],
                [.. set.Navigations.Select(navigation => new SetNavigation
                {
                    Name = navigation.Name,
                    Entity = navigation.Definition.Expression,
                    EntityProperty = navigation.Definition.Property,
                    Target = mappings[navigation.Target!],
                    IsCollection = navigation.Definition.IsCollection,
                    IsNullable = navigation.IsNullable,
                    ForeignKey = navigation.ForeignKey is null ? null : properties[navigation.ForeignKey],
                })],
                [.. set.Definition.Rules.Select(rule => ResolveRule(set, rule, properties))],
                [.. set.Definition.Validators]);
        }

        return new ServerModel(model, [.. mappings.Values.OrderBy(mapping => mapping.Name, StringComparer.Ordinal)], contextType, limits);
    }

    internal static InvalidOperationException Invalid(string? setName, Type entityType, string problem) =>
        new($"The set {(setName is null ? "" : $"'{setName}' ")}of {entityType.Name} {problem}.");

    private static InvalidOperationException InvalidValue(string typeName, string problem) => new($"The value type {typeName} {problem}.");

    private static ResolvedSet Resolve(SetDefinition definition, IModel efModel, Type contextType, ResolveContext context)
    {
        var name = definition.Name ?? throw Invalid(null, definition.EntityType, "has no name. Call ToSet(...)");
        var entity = efModel.FindEntityType(definition.EntityType)
            ?? throw Invalid(name, definition.EntityType, $"isn't an entity type of {contextType.Name}");
        var set = new ResolvedSet(definition, name, entity);

        var keyProperties = definition.Key
            ?? entity.FindPrimaryKey()?.Properties.Select(property => property.PropertyInfo ?? throw Invalid(name, definition.EntityType, $"has the shadow key property '{property.Name}'. Call HasKey(...) with CLR properties")).ToList()
            ?? throw Invalid(name, definition.EntityType, "has no key. Call HasKey(...)");

        // Keys come first, in key order; the other members keep the order they were configured in.
        var members = keyProperties
            .Select(property => definition.Members.Find(member => member.Property == property) ?? new MemberDefinition(property, null, PropertyLambda(definition.EntityType, property)))
            .Concat(definition.Members)
            .Distinct()
            .ToList();
        var writes = (definition.Writes & (WriteOperations.Insert | WriteOperations.Update)) != 0;
        foreach (var member in members)
        {
            if (member.ValueType is not null)
            {
                set.Properties.Add(ResolveValueUse(member, entity, definition.EntityType, writes && !member.IsReadOnly, problem => Invalid(name, definition.EntityType, problem), context, depth: 1));
                continue;
            }

            var isKey = member.Property is { } info && keyProperties.Contains(info);
            set.Properties.Add(ResolveProperty(set, member, isKey, context.Enums));
        }

        if ((definition.Rules.Count > 0 || definition.Validators.Count > 0) && (definition.Writes & (WriteOperations.Insert | WriteOperations.Update)) == 0)
        {
            throw Invalid(name, definition.EntityType, "has Must rules or validators, but allows neither inserts nor updates, so they would never run");
        }

        if (definition.ConcurrencyToken is { } token)
        {
            var efProperty = entity.FindProperty(token.Property!);
            if (efProperty is null || !efProperty.IsConcurrencyToken)
            {
                throw Invalid(name, definition.EntityType, $"uses '{token.Property!.Name}' as concurrency token, but it isn't one in the EF Core model. Configure it with IsConcurrencyToken() or IsRowVersion()");
            }
        }

        return set;
    }

    private static ResolvedProperty ResolveProperty(ResolvedSet set, MemberDefinition member, bool isKey, Dictionary<string, Type> enums)
    {
        var entityType = set.Definition.EntityType;
        LambdaExpression binding;
        bool isNullable;
        if (member.Property is { } info)
        {
            var efProperty = set.Entity.FindProperty(info)
                ?? throw Invalid(set.Name, entityType, $"exposes '{info.Name}', which isn't a property mapped by EF Core. Use HasOne/HasMany for navigations or Computed(...) for other values");
            binding = PropertyLambda(entityType, info);
            isNullable = efProperty.IsNullable;
        }
        else
        {
            binding = member.Expression;
            var type = binding.ReturnType;
            isNullable = ScalarTypes.GetCollectionElementType(type) is null && (type.IsValueType ? Nullable.GetUnderlyingType(type) is not null : !member.IsRequired);
        }

        var (typeName, elementType, underlying) = ResolveScalar(member, binding.ReturnType, enums, problem => Invalid(set.Name, entityType, problem));

        var isToken = set.Definition.ConcurrencyToken == member;
        var writable = !isKey && !isToken && member.Property is not null && !member.IsReadOnly
            && (set.Definition.Writes & (WriteOperations.Insert | WriteOperations.Update)) != 0;
        if (writable && member.Property!.SetMethod?.IsPublic != true)
        {
            throw Invalid(set.Name, entityType, $"exposes the writable property '{member.Property.Name}', which has no public setter. Mark it IsReadOnly()");
        }

        var rules = member.Rules.ToModel();
        if (rules is not null && !writable)
        {
            throw Invalid(set.Name, entityType, $"has validation rules on '{member.Describe()}', which isn't writable. Rules apply to inserts and updates, so keys, the concurrency token, read-only members and sets without inserts or updates can't have them");
        }

        CheckRangeBounds(member, rules, underlying, problem => Invalid(set.Name, entityType, problem));
        return new ResolvedProperty(member)
        {
            Rules = rules,
            Name = member.WireName ?? member.ComputedName ?? JsonNamingPolicy.CamelCase.ConvertName(member.Property!.Name),
            Binding = binding,
            TypeName = typeName,
            ElementType = elementType,
            IsNullable = isNullable,
            IsKey = isKey,
            IsWritable = writable,
            IsSortable = member.IsSortable || isKey,
            IsFilterable = !member.IsNotFilterable,
            IsGroupable = member.IsGroupable,
            IsAggregatable = member.IsAggregatable,
        };
    }

    private static (string TypeName, Type? ElementType, Type Underlying) ResolveScalar(
        MemberDefinition member, Type clrType, Dictionary<string, Type> enums, Func<string, InvalidOperationException> invalid)
    {
        var elementType = ScalarTypes.GetCollectionElementType(clrType);
        var scalarType = elementType ?? clrType;
        if (!ScalarTypes.IsScalar(scalarType) || (elementType is not null && elementType == typeof(byte[])))
        {
            throw invalid($"exposes '{member.Describe()}' of unsupported type {clrType.Name}");
        }

        var underlying = Nullable.GetUnderlyingType(scalarType) ?? scalarType;
        var typeName = ScalarTypes.GetName(underlying) ?? underlying.Name;
        if (underlying.IsEnum && !enums.TryAdd(typeName, underlying) && enums[typeName] != underlying)
        {
            throw invalid($"exposes the enum {underlying.FullName}, but another enum named {typeName} is exposed too");
        }

        return (typeName, elementType, underlying);
    }

    private static void CheckRangeBounds(MemberDefinition member, RestPropertyRules? rules, Type underlying, Func<string, InvalidOperationException> invalid)
    {
        foreach (var bound in (ReadOnlySpan<object?>)[rules?.Min, rules?.Max])
        {
            if (bound is not null && bound.GetType() != underlying)
            {
                throw invalid($"has HasRange on '{member.Describe()}' with {bound.GetType().Name} bounds, but the property is {underlying.Name}");
            }
        }
    }

    /// <summary>
    /// Resolves a property holding a value type (on an entity, or on another value type): an EF complex property or owned
    /// reference whose CLR type is a configured value type. A writable use needs every EF-mapped member exposed and a
    /// way to construct the value.
    /// </summary>
    private static ResolvedProperty ResolveValueUse(
        MemberDefinition member, ITypeBase owner, Type ownerClrType, bool writable, Func<string, InvalidOperationException> invalid, ResolveContext context, int depth)
    {
        var info = member.Property!;
        var valueClrType = member.ValueType!;
        if (!context.ValueDefinitions.TryGetValue(valueClrType, out var definition))
        {
            throw invalid($"exposes '{info.Name}' with Value(...), but {valueClrType.Name} isn't a configured value type. Add an IValueTypeConfiguration<{valueClrType.Name}>");
        }

        ITypeBase efType;
        bool isNullable;
        if (owner.FindComplexProperty(info) is { IsCollection: false } complex)
        {
            efType = complex.ComplexType;
            isNullable = complex.IsNullable;
        }
        else if (owner is IEntityType entity && entity.FindNavigation(info) is { IsCollection: false } navigation && navigation.TargetEntityType.IsOwned())
        {
            efType = navigation.TargetEntityType;
            isNullable = !navigation.ForeignKey.IsRequiredDependent;
        }
        else
        {
            throw invalid($"exposes '{info.Name}' with Value(...), but it isn't a complex property or an owned reference (OwnsOne) in the EF Core model");
        }

        if (depth > RestModel.MaxValueTypeDepth)
        {
            throw invalid($"exposes '{info.Name}', which nests value types more than {RestModel.MaxValueTypeDepth} levels deep");
        }

        if (writable && info.SetMethod?.IsPublic != true)
        {
            throw invalid($"exposes the writable value '{info.Name}', which has no public setter. Mark it IsReadOnly()");
        }

        var valueType = ResolveValueType(definition, efType, writable, context, depth);
        return new ResolvedProperty(member)
        {
            Name = member.WireName ?? JsonNamingPolicy.CamelCase.ConvertName(info.Name),
            Binding = PropertyLambda(ownerClrType, info),
            TypeName = valueType.Name,
            IsNullable = isNullable,

            // A nested value is written with the value holding it, so only the outermost property is writable.
            IsWritable = writable && depth == 1,
            IsFilterable = !member.IsNotFilterable,
            ValueType = valueType,
        };
    }

    private static ResolvedValueType ResolveValueType(ValueTypeDefinition definition, ITypeBase efType, bool writable, ResolveContext context, int depth)
    {
        var name = definition.TypeName ?? definition.ClrType.Name;
        InvalidOperationException Fail(string problem) => InvalidValue(name, problem);
        if (definition.Members.Count == 0)
        {
            throw Fail("exposes no members. Call Property(...) or Value(...)");
        }

        var properties = new List<ResolvedProperty>();
        foreach (var member in definition.Members)
        {
            properties.Add(member.ValueType is not null
                ? ResolveValueUse(member, efType, definition.ClrType, writable, Fail, context, depth + 1)
                : ResolveValueMember(member, efType, context.Enums, Fail));
        }

        if (writable)
        {
            var exposed = definition.Members.Select(member => member.Property).ToHashSet();
            var hidden = efType.GetProperties().Where(property => property.PropertyInfo is not null && !property.IsPrimaryKey()).Select(property => property.PropertyInfo!)
                .Concat(efType.GetComplexProperties().Select(property => property.PropertyInfo).OfType<PropertyInfo>())
                .Concat(efType is IEntityType entity ? entity.GetNavigations().Where(navigation => navigation.TargetEntityType.IsOwned()).Select(navigation => navigation.PropertyInfo).OfType<PropertyInfo>() : [])
                .FirstOrDefault(property => !exposed.Contains(property));
            if (hidden is not null)
            {
                throw Fail($"is written by a writable property, but doesn't expose '{hidden.Name}'. Writes replace the whole value, so expose every member EF Core maps, or mark the properties using it IsReadOnly()");
            }
        }

        if (context.ValueTypes.TryGetValue(definition.ClrType, out var existing))
        {
            // Every use maps the same members; only nullability and types can differ, and the model has one shape.
            if (!existing.Properties.Select(property => (property.Name, property.TypeName, property.IsNullable))
                    .SequenceEqual(properties.Select(property => (property.Name, property.TypeName, property.IsNullable))))
            {
                throw Fail("is mapped differently where it's used: its members' types or nullability differ between the EF Core mappings");
            }

            if (writable && existing.Construction is null)
            {
                existing.Construction = Construct(definition, existing.Properties, Fail);
            }

            return existing;
        }

        var resolved = new ResolvedValueType(definition, name, properties) { Construction = writable ? Construct(definition, properties, Fail) : null };
        context.ValueTypes.Add(definition.ClrType, resolved);
        return resolved;
    }

    private static ResolvedProperty ResolveValueMember(MemberDefinition member, ITypeBase efType, Dictionary<string, Type> enums, Func<string, InvalidOperationException> invalid)
    {
        var info = member.Property!;
        var efProperty = efType.FindProperty(info)
            ?? throw invalid($"exposes '{info.Name}', which isn't a property EF Core maps on the value. Use Value(...) for nested value types");
        var (typeName, elementType, underlying) = ResolveScalar(member, info.PropertyType, enums, invalid);
        if (elementType is not null)
        {
            throw invalid($"exposes the collection '{info.Name}', but value type members can't be collections");
        }

        var rules = member.Rules.ToModel();
        CheckRangeBounds(member, rules, underlying, invalid);
        return new ResolvedProperty(member)
        {
            Rules = rules,
            Name = member.WireName ?? JsonNamingPolicy.CamelCase.ConvertName(info.Name),
            Binding = PropertyLambda(efType.ClrType, info),
            TypeName = typeName,
            IsNullable = efProperty.IsNullable,
            IsSortable = member.IsSortable,
            IsFilterable = !member.IsNotFilterable,
            IsGroupable = member.IsGroupable,
            IsAggregatable = member.IsAggregatable,
        };
    }

    /// <summary>
    /// Finds how to build a value from its members: the public constructor with the most parameters that all match a
    /// member by name (ignoring case) and type, with every other member settable (including <c>init</c>).
    /// </summary>
    private static ResolvedConstruction Construct(ValueTypeDefinition definition, List<ResolvedProperty> properties, Func<string, InvalidOperationException> invalid)
    {
        (ConstructorInfo Constructor, List<ResolvedProperty> Arguments)? best = null;
        foreach (var constructor in definition.ClrType.GetConstructors())
        {
            var arguments = new List<ResolvedProperty>();
            foreach (var parameter in constructor.GetParameters())
            {
                if (properties.Find(property => string.Equals(property.Member.Property!.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)
                        && property.Member.Property!.PropertyType == parameter.ParameterType) is not { } match || arguments.Contains(match))
                {
                    arguments = null;
                    break;
                }

                arguments.Add(match);
            }

            if (arguments is not null && (best is null || arguments.Count > best.Value.Arguments.Count))
            {
                best = (constructor, arguments);
            }
        }

        if (best is not { } chosen)
        {
            throw invalid("can't be constructed for writes. Give it a public constructor whose parameters match its members by name, or a parameterless one");
        }

        var setters = properties.Where(property => !chosen.Arguments.Contains(property)).ToList();
        if (setters.Find(property => property.Member.Property!.SetMethod?.IsPublic != true) is { } readOnly)
        {
            throw invalid($"can't be constructed for writes: '{readOnly.Member.Property!.Name}' is neither a constructor parameter nor settable");
        }

        return new ResolvedConstruction(chosen.Constructor, chosen.Arguments, setters);
    }

    private static RestValueTypeModel ToModel(ResolvedValueType valueType) => new()
    {
        Name = valueType.Name,
        Properties = [.. valueType.Properties.Select(ToModel)],
    };

    private static SetRule ResolveRule(ResolvedSet set, RuleDefinition.Custom rule, Dictionary<ResolvedProperty, SetProperty> properties)
    {
        if (rule.Member is not { } member)
        {
            return new SetRule(null, rule.IsValid, rule.Code, OnlyWhenSent: false);
        }

        var property = set.Properties.Find(property => property.Member.Property == member)
            ?? throw Invalid(set.Name, set.Definition.EntityType, $"has a Must rule on '{member.Name}', which isn't exposed. Call Property(...)");

        // A member rule runs when the batch sends the member, which only writable members can be.
        if (rule.OnlyWhenSent && !property.IsWritable)
        {
            throw Invalid(set.Name, set.Definition.EntityType, $"has a Must rule on '{member.Name}', which isn't writable, so it would never run");
        }

        return new SetRule(properties[property], rule.IsValid, rule.Code, rule.OnlyWhenSent);
    }

    private static void ResolveNavigations(ResolvedSet set, List<ResolvedSet> sets)
    {
        var entityType = set.Definition.EntityType;
        foreach (var definition in set.Definition.Navigations)
        {
            var efNavigation = set.Entity.FindNavigation(definition.Property);
            if (efNavigation is null || efNavigation.IsCollection != definition.IsCollection)
            {
                throw Invalid(set.Name, entityType, $"exposes '{definition.Property.Name}' with {(definition.IsCollection ? "HasMany" : "HasOne")}, but it isn't a {(definition.IsCollection ? "collection" : "reference")} navigation in the EF Core model");
            }

            var targets = sets.Where(candidate => candidate.Definition.EntityType == definition.TargetEntity).ToList();
            if (targets.Count != 1)
            {
                throw Invalid(set.Name, entityType, targets.Count == 0
                    ? $"exposes the navigation '{definition.Property.Name}' to {definition.TargetEntity.Name}, which has no set"
                    : $"exposes the navigation '{definition.Property.Name}' to {definition.TargetEntity.Name}, which has several sets");
            }

            ResolvedProperty? foreignKey = null;
            if (definition.ForeignKey is { } foreignKeyInfo)
            {
                foreignKey = set.Properties.Find(property => property.Member.Property == foreignKeyInfo)
                    ?? throw Invalid(set.Name, entityType, $"uses '{foreignKeyInfo.Name}' as the foreign key of '{definition.Property.Name}', but it isn't exposed. Call Property(...)");
                if (!efNavigation.IsOnDependent || efNavigation.ForeignKey.Properties is not [{ PropertyInfo: { } efForeignKey }] || efForeignKey != foreignKeyInfo)
                {
                    throw Invalid(set.Name, entityType, $"uses '{foreignKeyInfo.Name}' as the foreign key of '{definition.Property.Name}', but it isn't that navigation's foreign key in the EF Core model");
                }
            }

            set.Navigations.Add(new ResolvedNavigation(definition)
            {
                Name = definition.WireName ?? JsonNamingPolicy.CamelCase.ConvertName(definition.Property.Name),
                Target = targets[0],
                IsNullable = !definition.IsCollection && (!efNavigation.IsOnDependent || !efNavigation.ForeignKey.IsRequired),
                ForeignKey = foreignKey,
            });
        }
    }

    private static RestSetModel ToModel(ResolvedSet set) => new()
    {
        Name = set.Name,
        TypeName = set.Definition.TypeName ?? set.Definition.EntityType.Name,
        Key = [.. set.Properties.Where(property => property.IsKey).Select(property => property.Name)],
        ConcurrencyToken = set.Properties.Find(property => property.Member == set.Definition.ConcurrencyToken)?.Name,
        Writes = set.Definition.Writes,
        Pagination = set.Definition.Pagination.Model,
        Properties = [.. set.Properties.Select(ToModel)],
        Navigations = [.. set.Navigations.Select(navigation => new RestNavigationModel
        {
            Name = navigation.Name,
            Target = navigation.Target!.Name,
            IsCollection = navigation.Definition.IsCollection,
            IsNullable = navigation.IsNullable,
            ForeignKey = navigation.ForeignKey is null ? [] : [navigation.ForeignKey.Name],
        })],
    };

    private static RestPropertyModel ToModel(ResolvedProperty property) => new()
    {
        Name = property.Name,
        Type = property.TypeName,
        IsNullable = property.IsNullable,
        IsCollection = property.ElementType is not null,
        IsWritable = property.IsWritable,
        IsSortable = property.IsSortable,
        IsFilterable = property.IsFilterable,
        Rules = property.Rules,
        IsGroupable = property.IsGroupable,
        IsAggregatable = property.IsAggregatable,
    };

    private static LambdaExpression PropertyLambda(Type entityType, PropertyInfo property)
    {
        var entity = Expression.Parameter(entityType, "e");
        return Expression.Lambda(Expression.Property(entity, property), entity);
    }

    private sealed class ResolvedSet(SetDefinition definition, string name, IEntityType entity)
    {
        public SetDefinition Definition { get; } = definition;

        public string Name { get; } = name;

        public IEntityType Entity { get; } = entity;

        public List<ResolvedProperty> Properties { get; } = [];

        public List<ResolvedNavigation> Navigations { get; } = [];
    }

    private sealed class ResolvedProperty(MemberDefinition member)
    {
        public MemberDefinition Member { get; } = member;

        public required string Name { get; init; }

        public required LambdaExpression Binding { get; init; }

        public required string TypeName { get; init; }

        public Type? ElementType { get; init; }

        public bool IsNullable { get; init; }

        public bool IsKey { get; init; }

        public bool IsWritable { get; init; }

        public bool IsSortable { get; init; }

        public bool IsFilterable { get; init; }

        public RestPropertyRules? Rules { get; init; }

        public bool IsGroupable { get; init; }

        public bool IsAggregatable { get; init; }

        public ResolvedValueType? ValueType { get; init; }

        public SetProperty ToSetProperty(Dictionary<ResolvedValueType, ServerValueType> valueTypes) => new()
        {
            ValueType = ValueType is null ? null : valueTypes[ValueType],
            IsGroupable = IsGroupable,
            IsAggregatable = IsAggregatable,
            Rules = Rules,
            Name = Name,
            Binding = Binding,
            EntityProperty = Member.Property,
            ElementType = ElementType,
            IsNullable = IsNullable,
            IsWritable = IsWritable,
            IsSortable = IsSortable,
            IsFilterable = IsFilterable,
        };
    }

    private sealed class ResolvedValueType(ValueTypeDefinition definition, string name, List<ResolvedProperty> properties)
    {
        public ValueTypeDefinition Definition { get; } = definition;

        public string Name { get; } = name;

        public List<ResolvedProperty> Properties { get; } = properties;

        public ResolvedConstruction? Construction { get; set; }
    }

    private sealed record ResolvedConstruction(ConstructorInfo Constructor, List<ResolvedProperty> Arguments, List<ResolvedProperty> Setters);

    /// <summary>What resolving every set collects: enums and value types (resolved once each, shared by their uses).</summary>
    private sealed class ResolveContext
    {
        public Dictionary<string, Type> Enums { get; } = new(StringComparer.Ordinal);

        public Dictionary<Type, ValueTypeDefinition> ValueDefinitions { get; } = [];

        public Dictionary<Type, ResolvedValueType> ValueTypes { get; } = [];
    }

    private sealed class ResolvedNavigation(NavigationDefinition definition)
    {
        public NavigationDefinition Definition { get; } = definition;

        public required string Name { get; init; }

        public ResolvedSet? Target { get; init; }

        public bool IsNullable { get; init; }

        public ResolvedProperty? ForeignKey { get; init; }
    }
}

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
    public static ServerModel Build(IServiceProvider services, IReadOnlyList<SetRegistration> registrations, Type? contextType, Protocol.QueryLimits limits)
    {
        if (contextType is null)
        {
            throw new InvalidOperationException("LinqToRest needs a DbContext. Call UseDbContext<TContext>().");
        }

        using var scope = services.CreateScope();
        var db = (DbContext)scope.ServiceProvider.GetRequiredService(contextType);
        return Build(registrations, contextType, db.Model, limits);
    }

    public static ServerModel Build(IReadOnlyList<SetRegistration> registrations, Type contextType, IModel efModel, Protocol.QueryLimits limits)
    {
        var definitions = registrations.Select(registration => (Registration: registration, Definition: registration.Configure())).ToList();
        var enums = new Dictionary<string, Type>(StringComparer.Ordinal);
        var resolved = definitions
            .Select(item => (item.Registration, Set: Resolve(item.Definition, efModel, contextType, enums)))
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
                enums.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new RestEnumModel { Name = pair.Key, Members = Enum.GetNames(pair.Value) }));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"The LinqToRest model is invalid: {exception.Message}", exception);
        }

        var mappings = resolved.ToDictionary(item => item.Set, item => item.Registration.Create(models[item.Set], item.Set.Definition, contextType));
        foreach (var (set, mapping) in mappings)
        {
            var properties = set.Properties.ToDictionary(property => property, property => property.ToSetProperty());
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
                })]);
        }

        return new ServerModel(model, [.. mappings.Values.OrderBy(mapping => mapping.Name, StringComparer.Ordinal)], contextType, limits);
    }

    internal static InvalidOperationException Invalid(string? setName, Type entityType, string problem) =>
        new($"The set {(setName is null ? "" : $"'{setName}' ")}of {entityType.Name} {problem}.");

    private static ResolvedSet Resolve(SetDefinition definition, IModel efModel, Type contextType, Dictionary<string, Type> enums)
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
        foreach (var member in members)
        {
            var isKey = member.Property is { } info && keyProperties.Contains(info);
            set.Properties.Add(ResolveProperty(set, member, isKey, enums));
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

        var clrType = binding.ReturnType;
        var elementType = ScalarTypes.GetCollectionElementType(clrType);
        var scalarType = elementType ?? clrType;
        if (!ScalarTypes.IsScalar(scalarType) || (elementType is not null && elementType == typeof(byte[])))
        {
            throw Invalid(set.Name, entityType, $"exposes '{member.Describe()}' of unsupported type {clrType.Name}");
        }

        var underlying = Nullable.GetUnderlyingType(scalarType) ?? scalarType;
        var typeName = ScalarTypes.GetName(underlying) ?? underlying.Name;
        if (underlying.IsEnum && !enums.TryAdd(typeName, underlying) && enums[typeName] != underlying)
        {
            throw Invalid(set.Name, entityType, $"exposes the enum {underlying.FullName}, but another enum named {typeName} is exposed too");
        }

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

        foreach (var bound in (ReadOnlySpan<object?>)[rules?.Min, rules?.Max])
        {
            if (bound is not null && bound.GetType() != underlying)
            {
                throw Invalid(set.Name, entityType, $"has HasRange on '{member.Describe()}' with {bound.GetType().Name} bounds, but the property is {underlying.Name}");
            }
        }

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
        };
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
        Properties = [.. set.Properties.Select(property => new RestPropertyModel
        {
            Name = property.Name,
            Type = property.TypeName,
            IsNullable = property.IsNullable,
            IsCollection = property.ElementType is not null,
            IsWritable = property.IsWritable,
            IsSortable = property.IsSortable,
            IsFilterable = property.IsFilterable,
            Rules = property.Rules,
        })],
        Navigations = [.. set.Navigations.Select(navigation => new RestNavigationModel
        {
            Name = navigation.Name,
            Target = navigation.Target!.Name,
            IsCollection = navigation.Definition.IsCollection,
            IsNullable = navigation.IsNullable,
            ForeignKey = navigation.ForeignKey is null ? [] : [navigation.ForeignKey.Name],
        })],
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

        public SetProperty ToSetProperty() => new()
        {
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

    private sealed class ResolvedNavigation(NavigationDefinition definition)
    {
        public NavigationDefinition Definition { get; } = definition;

        public required string Name { get; init; }

        public ResolvedSet? Target { get; init; }

        public bool IsNullable { get; init; }

        public ResolvedProperty? ForeignKey { get; init; }
    }
}

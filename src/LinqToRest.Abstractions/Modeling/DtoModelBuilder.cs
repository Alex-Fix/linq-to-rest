using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinqToRest.Modeling;

/// <summary>Builds a <see cref="DtoModel"/> from DTO type configurations, like EF Core's model builder.</summary>
public sealed partial class DtoModelBuilder
{
    private readonly Dictionary<Type, object> _builders = [];
    private readonly Dictionary<Type, DtoTypeDefinition> _definitions = [];

    /// <summary>Configures a DTO type inline.</summary>
    /// <param name="configure">Configures the DTO type.</param>
    /// <typeparam name="TDto">The DTO type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoModelBuilder Dto<TDto>(Action<DtoTypeBuilder<TDto>> configure)
        where TDto : class, IDto
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(GetBuilder<TDto>());
        return this;
    }

    /// <summary>Applies a DTO type configuration.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <typeparam name="TDto">The DTO type.</typeparam>
    /// <returns>The same builder.</returns>
    public DtoModelBuilder ApplyConfiguration<TDto>(IDtoTypeConfiguration<TDto> configuration)
        where TDto : class, IDto
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Configure(GetBuilder<TDto>());
        return this;
    }

    /// <summary>
    /// Applies every non-abstract <see cref="IDtoTypeConfiguration{TDto}"/> with a parameterless constructor found in
    /// <paramref name="assembly"/>.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The same builder.</returns>
    public DtoModelBuilder ApplyConfigurationsFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var apply = typeof(DtoModelBuilder).GetMethod(nameof(ApplyConfiguration))!;
        var configurations = assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsGenericTypeDefinition: false } && type.GetConstructor(Type.EmptyTypes) is not null)
            .SelectMany(type => type.GetInterfaces()
                .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IDtoTypeConfiguration<>))
                .Select(contract => (Type: type, Dto: contract.GetGenericArguments()[0])))
            .OrderBy(configuration => configuration.Type.FullName, StringComparer.Ordinal);

        foreach (var (type, dto) in configurations)
        {
            apply.MakeGenericMethod(dto).Invoke(this, [Activator.CreateInstance(type)]);
        }

        return this;
    }

    /// <summary>Validates the configuration and builds the immutable model.</summary>
    /// <returns>The model.</returns>
    /// <exception cref="InvalidOperationException">The configuration is invalid; the message says why.</exception>
    public DtoModel Build()
    {
        if (_definitions.Count == 0)
        {
            throw new InvalidOperationException("The DTO model has no types. Configure at least one DTO.");
        }

        var types = new Dictionary<Type, DtoType>();
        var setNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in _definitions.Values.OrderBy(definition => definition.ClrType.FullName, StringComparer.Ordinal))
        {
            types.Add(definition.ClrType, CreateType(definition, setNames));
        }

        var nullability = new NullabilityInfoContext();
        foreach (var definition in _definitions.Values)
        {
            InitializeType(definition, types[definition.ClrType], types, nullability);
        }

        foreach (var definition in _definitions.Values)
        {
            InitializeForeignKeys(definition, types[definition.ClrType]);
        }

        return new DtoModel([.. types.Values]);
    }

    private DtoTypeBuilder<TDto> GetBuilder<TDto>()
        where TDto : class, IDto
    {
        if (!_builders.TryGetValue(typeof(TDto), out var builder))
        {
            var created = new DtoTypeBuilder<TDto>();
            _builders.Add(typeof(TDto), created);
            _definitions.Add(typeof(TDto), created.Definition);
            return created;
        }

        return (DtoTypeBuilder<TDto>)builder;
    }


    private static DtoType CreateType(DtoTypeDefinition definition, HashSet<string> setNames)
    {
        var type = definition.ClrType;
        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw Invalid(type, "must have a public parameterless constructor");
        }

        var setName = definition.SetName ?? throw Invalid(type, "has no set name. Call ToSet(...)");
        if (!SetNamePattern().IsMatch(setName))
        {
            throw Invalid(type, $"has an invalid set name '{setName}'. Use a lowercase letter followed by letters or digits, at most 64 characters");
        }

        if (!setNames.Add(setName))
        {
            throw Invalid(type, $"uses the set name '{setName}', which another DTO already uses");
        }

        var pagination = definition.Pagination;
        if (pagination.Modes == PaginationModes.None || (pagination.Modes & ~PaginationModes.All) != 0)
        {
            throw Invalid(type, "must allow at least one valid pagination mode");
        }

        if (pagination.MaxPageSize < 1 || pagination.DefaultPageSize < 1 || pagination.DefaultPageSize > pagination.MaxPageSize)
        {
            throw Invalid(type, "needs 1 <= DefaultPageSize <= MaxPageSize");
        }

        if (pagination.MaxSkip < 0)
        {
            throw Invalid(type, "needs MaxSkip >= 0");
        }

        return new DtoType(type, setName, new DtoPagination(pagination.Modes, pagination.DefaultPageSize, pagination.MaxPageSize, pagination.MaxSkip));
    }

    private static void InitializeType(
        DtoTypeDefinition definition,
        DtoType type,
        Dictionary<Type, DtoType> types,
        NullabilityInfoContext nullability)
    {
        var properties = new List<DtoProperty>();
        var navigations = new List<DtoNavigation>();
        var byInfo = new Dictionary<PropertyInfo, DtoProperty>();
        var wireNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var info in GetMembers(definition))
        {
            var wireName = JsonNamingPolicy.CamelCase.ConvertName(info.Name);
            if (!wireNames.Add(wireName))
            {
                throw Invalid(type.ClrType, $"has two members with the wire name '{wireName}'");
            }

            var isNullable = Nullable.GetUnderlyingType(info.PropertyType) is not null
                || (!info.PropertyType.IsValueType && nullability.Create(info).ReadState != NullabilityState.NotNull);

            if (TryGetNavigationTarget(info.PropertyType, out var targetClrType, out var isCollection))
            {
                if (!types.TryGetValue(targetClrType, out var target))
                {
                    throw Invalid(type.ClrType, $"has the navigation '{info.Name}' to {targetClrType.Name}, which isn't configured in the model");
                }

                var navigation = new DtoNavigation(type, info, wireName, target, isCollection, isNullable && !isCollection);
                if (definition.Navigations.TryGetValue(info, out var configured) && configured.IsCollection != isCollection)
                {
                    throw Invalid(type.ClrType, $"configures '{info.Name}' with {(configured.IsCollection ? "HasMany" : "HasOne")}, but it's a {(isCollection ? "collection" : "reference")}");
                }

                navigations.Add(navigation);
                continue;
            }

            var elementType = ScalarTypes.GetCollectionElementType(info.PropertyType);
            if (elementType is not null ? !ScalarTypes.IsScalar(elementType) || elementType == typeof(byte[]) : !ScalarTypes.IsScalar(info.PropertyType))
            {
                throw Invalid(type.ClrType, $"has the property '{info.Name}' of unsupported type {info.PropertyType.Name}. Ignore it or change its type");
            }

            var property = new DtoProperty(type, info, wireName, elementType, isNullable);
            var settings = definition.Properties.GetValueOrDefault(info);
            property.IsReadOnly = settings?.IsReadOnly ?? false;
            property.IsFilterable = !(settings?.IsNotFilterable ?? false);
            property.IsSortable = settings?.IsSortable ?? false;
            if (property.IsSortable && (property.IsCollection || property.ClrType == typeof(byte[])))
            {
                throw Invalid(type.ClrType, $"marks '{info.Name}' as sortable, but collections and binary values can't be sorted");
            }

            properties.Add(property);
            byInfo.Add(info, property);
        }

        foreach (var configured in definition.Properties.Keys.Where(info => !byInfo.ContainsKey(info)))
        {
            throw Invalid(type.ClrType, $"configures '{configured.Name}' with Property(...), but it isn't a scalar or primitive-collection property of the DTO");
        }

        foreach (var configured in definition.Navigations.Keys.Where(info => navigations.TrueForAll(navigation => navigation.PropertyInfo != info)))
        {
            throw Invalid(type.ClrType, $"configures '{configured.Name}' as a navigation, but it isn't a navigation to a configured DTO");
        }

        var key = (definition.Key ?? throw Invalid(type.ClrType, "has no key. Call HasKey(...)"))
            .Select(info => byInfo.GetValueOrDefault(info) ?? throw Invalid(type.ClrType, $"uses '{info.Name}' as key, but it isn't a scalar property"))
            .ToList();
        foreach (var property in key)
        {
            if (property.IsNullable || property.IsCollection || property.ClrType == typeof(byte[]))
            {
                throw Invalid(type.ClrType, $"uses '{property.Name}' as key, but keys must be non-nullable scalars");
            }

            property.IsKey = true;
            property.IsSortable = true;
        }

        DtoProperty? token = null;
        if (definition.ConcurrencyToken is { } tokenInfo)
        {
            token = byInfo.GetValueOrDefault(tokenInfo);
            if (token is null || token.IsCollection || token.IsKey)
            {
                throw Invalid(type.ClrType, $"uses '{tokenInfo.Name}' as concurrency token, but it must be a scalar, non-key property");
            }

            token.IsConcurrencyToken = true;
        }

        type.Initialize(properties, navigations, key, token);
    }

    private static void InitializeForeignKeys(DtoTypeDefinition definition, DtoType type)
    {
        foreach (var navigation in type.Navigations)
        {
            var foreignKeyInfo = definition.Navigations.GetValueOrDefault(navigation.PropertyInfo)?.ForeignKey;
            if (foreignKeyInfo is null)
            {
                continue;
            }

            var foreignKey = type.Properties.FirstOrDefault(property => property.PropertyInfo == foreignKeyInfo);
            var targetKey = navigation.Target.Key;
            if (foreignKey is null || targetKey.Count != 1
                || (Nullable.GetUnderlyingType(foreignKey.ClrType) ?? foreignKey.ClrType) != targetKey[0].ClrType)
            {
                throw Invalid(type.ClrType, $"uses '{foreignKeyInfo.Name}' as foreign key of '{navigation.Name}', but it must be a scalar property of the same type as the single key of {navigation.Target}");
            }

            navigation.ForeignKey = foreignKey;
        }
    }

    private static IEnumerable<PropertyInfo> GetMembers(DtoTypeDefinition definition) =>
        definition.ClrType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(info => info.GetIndexParameters().Length == 0 && info.GetMethod?.IsPublic == true && info.SetMethod?.IsPublic == true)
            .Where(info => !definition.Ignored.Contains(info))
            .OrderBy(info => info.MetadataToken);

    private static bool TryGetNavigationTarget(Type propertyType, out Type target, out bool isCollection)
    {
        if (typeof(IDto).IsAssignableFrom(propertyType))
        {
            (target, isCollection) = (propertyType, false);
            return true;
        }

        if (ScalarTypes.GetCollectionElementType(propertyType) is { } element && typeof(IDto).IsAssignableFrom(element) && !propertyType.IsArray)
        {
            (target, isCollection) = (element, true);
            return true;
        }

        (target, isCollection) = (typeof(object), false);
        return false;
    }

    private static InvalidOperationException Invalid(Type type, string problem) => new($"DTO {type.Name} {problem}.");

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SetNamePattern();
}

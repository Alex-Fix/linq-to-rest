using System.Reflection;
using System.Text.Json;
using LinqToRest.Modeling;

namespace LinqToRest.Client;

/// <summary>
/// A model document bound to the client classes of its sets. Generated contexts create one; it's immutable and
/// shared by every context of that type.
/// </summary>
public sealed class ClientModel
{
    private readonly Dictionary<Type, ClientSet> _byType;

    private ClientModel(RestModel document, List<ClientSet> sets)
    {
        Document = document;
        Sets = sets;
        _byType = sets.ToDictionary(set => set.ClrType);
    }

    /// <summary>Gets the model document.</summary>
    public RestModel Document { get; }

    internal IReadOnlyList<ClientSet> Sets { get; }

    /// <summary>
    /// Binds a model document to client classes: each set's properties and navigations to the public read-write
    /// properties whose camelCase names are the wire names. Other properties are ignored.
    /// </summary>
    /// <param name="document">The model document.</param>
    /// <param name="types">The class of each set, by set name.</param>
    /// <returns>The bound model.</returns>
    /// <exception cref="InvalidOperationException">A class doesn't match its set; the message says why.</exception>
    public static ClientModel Bind(RestModel document, IReadOnlyDictionary<string, Type> types)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(types);
        foreach (var name in types.Keys.Where(name => document.FindSet(name) is null))
        {
            throw new InvalidOperationException($"The model has no set '{name}'.");
        }

        var sets = new Dictionary<string, ClientSet>(StringComparer.Ordinal);
        foreach (var set in document.Sets)
        {
            var type = types.GetValueOrDefault(set.Name) ?? throw new InvalidOperationException($"No class is given for the set '{set.Name}'.");
            if (type.IsAbstract || type.IsValueType || type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw Mismatch(set, type, "must be a class with a public parameterless constructor");
            }

            if (sets.Values.Any(other => other.ClrType == type))
            {
                throw Mismatch(set, type, "is bound to another set too");
            }

            sets.Add(set.Name, new ClientSet(set, type, [.. set.Properties.Select(property => BindProperty(document, set, type, property))]));
        }

        foreach (var set in sets.Values)
        {
            set.Initialize([.. set.Model.Navigations.Select(navigation => BindNavigation(set, navigation, sets))]);
        }

        return new ClientModel(document, [.. sets.Values]);
    }

    internal ClientSet? FindSet(Type type) => _byType.GetValueOrDefault(type);

    private static ClientProperty BindProperty(RestModel document, RestSetModel set, Type type, RestPropertyModel model)
    {
        var info = FindClrProperty(set, type, model.Name);
        var elementType = ScalarTypes.GetCollectionElementType(info.PropertyType);
        if (model.IsCollection != elementType is not null)
        {
            throw Mismatch(set, type, $"has '{info.Name}' as {(elementType is null ? "a single value" : "a collection")}, but the model says otherwise");
        }

        var valueType = elementType ?? info.PropertyType;
        var underlying = Nullable.GetUnderlyingType(valueType) ?? valueType;
        var fits = ScalarTypes.FromName(model.Type) is { } scalar
            ? underlying == scalar
            : underlying.IsEnum && underlying.Name == model.Type && document.FindEnum(model.Type) is { } enumModel
                && Enum.GetNames(underlying).Order(StringComparer.Ordinal).SequenceEqual(enumModel.Members.Order(StringComparer.Ordinal));
        if (!fits)
        {
            throw Mismatch(set, type, $"has '{info.Name}' of type {info.PropertyType.Name}, but the model says {model.Type}");
        }

        if (model is { IsNullable: true, IsCollection: false } && valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null)
        {
            throw Mismatch(set, type, $"has '{info.Name}' as non-nullable, but the model says it can be null");
        }

        return new ClientProperty(info, model, elementType)
        {
            IsKey = set.Key.Contains(model.Name),
            IsConcurrencyToken = set.ConcurrencyToken == model.Name,
        };
    }

    private static ClientNavigation BindNavigation(ClientSet set, RestNavigationModel model, Dictionary<string, ClientSet> sets)
    {
        var info = FindClrProperty(set.Model, set.ClrType, model.Name);
        var target = sets[model.Target];
        var targetType = model.IsCollection ? ScalarTypes.GetCollectionElementType(info.PropertyType) : info.PropertyType;
        if (targetType != target.ClrType)
        {
            throw Mismatch(set.Model, set.ClrType, $"has the navigation '{info.Name}' of type {info.PropertyType.Name}, but the model says {(model.IsCollection ? "a collection of " : "")}{target.ClrType.Name}");
        }

        return new ClientNavigation(info, model, target)
        {
            ForeignKey = model.ForeignKey is [var name] ? set.Properties.Single(property => property.WireName == name) : null,
        };
    }

    private static PropertyInfo FindClrProperty(RestSetModel set, Type type, string wireName) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(info => info.GetIndexParameters().Length == 0 && info.GetMethod?.IsPublic == true && info.SetMethod?.IsPublic == true
                && JsonNamingPolicy.CamelCase.ConvertName(info.Name) == wireName)
        ?? throw Mismatch(set, type, $"has no public read-write property for '{wireName}'");

    private static InvalidOperationException Mismatch(RestSetModel set, Type type, string problem) =>
        new($"{type.Name}, the class of the set '{set.Name}', {problem}.");
}

/// <summary>A set bound to its client class.</summary>
internal sealed class ClientSet(RestSetModel model, Type clrType, ClientProperty[] properties)
{
    private readonly Dictionary<string, ClientProperty> _properties = properties.ToDictionary(property => property.WireName, StringComparer.Ordinal);
    private Dictionary<string, ClientNavigation> _navigations = [];

    public RestSetModel Model { get; } = model;

    public Type ClrType { get; } = clrType;

    public string SetName => Model.Name;

    public RestPaginationModel Pagination => Model.Pagination;

    public ClientProperty[] Properties { get; } = properties;

    public ClientNavigation[] Navigations { get; private set; } = [];

    public ClientProperty[] Key { get; } = [.. model.Key.Select(name => properties.Single(property => property.WireName == name))];

    public ClientProperty? ConcurrencyToken { get; } = properties.SingleOrDefault(property => property.IsConcurrencyToken);

    public ClientProperty? FindProperty(string wireName) => _properties.GetValueOrDefault(wireName);

    public ClientNavigation? FindNavigation(string wireName) => _navigations.GetValueOrDefault(wireName);

    public bool Allows(WriteOperations operations) => Model.Allows(operations);

    public void Initialize(ClientNavigation[] navigations)
    {
        Navigations = navigations;
        _navigations = navigations.ToDictionary(navigation => navigation.WireName, StringComparer.Ordinal);
    }

    public override string ToString() => ClrType.Name;
}

/// <summary>A scalar or primitive-collection property bound to its client property.</summary>
internal sealed class ClientProperty(PropertyInfo info, RestPropertyModel model, Type? elementType)
{
    public PropertyInfo PropertyInfo { get; } = info;

    /// <summary>Gets the CLR property name.</summary>
    public string Name => PropertyInfo.Name;

    public string WireName => model.Name;

    public Type ClrType => PropertyInfo.PropertyType;

    public Type? ElementType { get; } = elementType;

    public bool IsCollection => ElementType is not null;

    public bool IsNullable => model.IsNullable;

    public bool IsKey { get; init; }

    public bool IsConcurrencyToken { get; init; }

    public bool IsWritable => model.IsWritable;

    public bool IsSortable => model.IsSortable;

    public bool IsFilterable => model.IsFilterable;

    public override string ToString() => $"{PropertyInfo.DeclaringType?.Name}.{Name}";
}

/// <summary>A navigation bound to its client property.</summary>
internal sealed class ClientNavigation(PropertyInfo info, RestNavigationModel model, ClientSet target)
{
    public PropertyInfo PropertyInfo { get; } = info;

    public string Name => PropertyInfo.Name;

    public string WireName => model.Name;

    public ClientSet Target { get; } = target;

    public bool IsCollection => model.IsCollection;

    public bool IsNullable => model.IsNullable;

    public ClientProperty? ForeignKey { get; init; }

    public override string ToString() => $"{PropertyInfo.DeclaringType?.Name}.{Name}";
}

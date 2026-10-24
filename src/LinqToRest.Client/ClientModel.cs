using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Protocol;

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
    /// Binds a model document to client classes through compile-time delegates, without reflection, so it works trimmed
    /// and with Native AOT. Every set and member of the document needs exactly one binding. Generated contexts call this.
    /// </summary>
    /// <param name="document">The model document.</param>
    /// <param name="sets">One binding per set.</param>
    /// <returns>The bound model.</returns>
    /// <exception cref="InvalidOperationException">A binding doesn't match its set; the message says why.</exception>
    public static ClientModel Bind(RestModel document, params SetBinding[] sets)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sets);
        foreach (var binding in sets)
        {
            ArgumentNullException.ThrowIfNull(binding, nameof(sets));
            if (document.FindSet(binding.SetName) is null)
            {
                throw new InvalidOperationException($"The model has no set '{binding.SetName}'.");
            }
        }

        var bound = new Dictionary<string, ClientSet>(StringComparer.Ordinal);
        var valueTypes = new Dictionary<ValueBinding, ClientValueType>();
        foreach (var set in document.Sets)
        {
            var matching = sets.Where(binding => binding.SetName == set.Name).ToList();
            var binding = matching.Count switch
            {
                0 => throw new InvalidOperationException($"No class is given for the set '{set.Name}'."),
                1 => matching[0],
                _ => throw new InvalidOperationException($"The set '{set.Name}' is bound twice."),
            };
            if (bound.Values.Any(other => other.ClrType == binding.ClrType))
            {
                throw Mismatch(set, binding.ClrType, "is bound to another set too");
            }

            foreach (var extra in binding.Members.Where(member => set.FindProperty(member.Name) is null && set.FindNavigation(member.Name) is null))
            {
                throw Mismatch(set, binding.ClrType, $"binds '{extra.Name}', which the model doesn't have");
            }

            var properties = set.Properties.Select(property => BindProperty(document, binding.Members, property, valueTypes, problem => Mismatch(set, binding.ClrType, problem)));
            bound.Add(set.Name, new ClientSet(set, binding.ClrType, binding.Create, [.. properties.Select(property => property.OfSet(set))]));
        }

        foreach (var set in bound.Values)
        {
            var binding = sets.Single(candidate => candidate.SetName == set.SetName);
            set.Initialize([.. set.Model.Navigations.Select(navigation => BindNavigation(set, binding, navigation, bound))]);
        }

        return new ClientModel(document, [.. bound.Values]);
    }

    /// <summary>
    /// Binds a model document to client classes by reflection: each set's properties and navigations to the public
    /// read-write properties whose camelCase names are the wire names. Other properties are ignored. For hand-written
    /// classes; generated contexts use the reflection-free overload.
    /// </summary>
    /// <param name="document">The model document.</param>
    /// <param name="types">The class of each set, by set name.</param>
    /// <returns>The bound model.</returns>
    /// <exception cref="InvalidOperationException">A class doesn't match its set; the message says why.</exception>
    [RequiresUnreferencedCode("Finds the classes' properties by reflection, which trimming can remove. Use the SetBinding overload (generated contexts do).")]
    [RequiresDynamicCode("Compiles property getters at run time. Use the SetBinding overload (generated contexts do).")]
    public static ClientModel Bind(RestModel document, IReadOnlyDictionary<string, Type> types)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(types);
        foreach (var name in types.Keys.Where(name => document.FindSet(name) is null))
        {
            throw new InvalidOperationException($"The model has no set '{name}'.");
        }

        var bindings = new List<SetBinding>();
        foreach (var set in document.Sets)
        {
            var type = types.GetValueOrDefault(set.Name) ?? throw new InvalidOperationException($"No class is given for the set '{set.Name}'.");
            if (type.IsAbstract || type.IsValueType || type.GetConstructor(Type.EmptyTypes) is null)
            {
                throw Mismatch(set, type, "must be a class with a public parameterless constructor");
            }

            bindings.Add(ReflectedBinding.For(document, set, type));
        }

        return Bind(document, [.. bindings]);
    }

    internal ClientSet? FindSet(Type type) => _byType.GetValueOrDefault(type);

    /// <summary>Binds a property of a set or of a value type (<paramref name="members"/> are its class's bindings).</summary>
    private static ClientProperty BindProperty(
        RestModel document, List<MemberBinding> members, RestPropertyModel model, Dictionary<ValueBinding, ClientValueType> valueTypes, Func<string, InvalidOperationException> mismatch)
    {
        var member = members.Find(candidate => candidate.Name == model.Name && candidate.Kind is MemberKind.Property or MemberKind.Values or MemberKind.Value)
            ?? throw mismatch($"has no binding for '{model.Name}'");
        if (document.FindValueType(model.Type) is { } valueModel)
        {
            if (member.Value is not { } valueBinding || valueBinding.TypeName != valueModel.Name)
            {
                throw mismatch($"binds '{member.Member}' {(member.Value is null ? "as a scalar" : $"to the value type '{member.Value.TypeName}'")}, but the model says {model.Type}");
            }

            return new ClientProperty(member, model) { ValueType = BindValueType(document, valueModel, valueBinding, valueTypes) };
        }

        var elementType = member.Kind == MemberKind.Values ? member.ElementType : null;
        if (member.Kind == MemberKind.Value || model.IsCollection != elementType is not null)
        {
            throw mismatch($"has '{member.Member}' as {(elementType is null ? "a single value" : "a collection")}, but the model says otherwise");
        }

        var valueType = elementType ?? member.ValueType;
        var underlying = Nullable.GetUnderlyingType(valueType) ?? valueType;
        var fits = ScalarTypes.FromName(model.Type) is { } scalar
            ? underlying == scalar
            : underlying.IsEnum && underlying.Name == model.Type && document.FindEnum(model.Type) is { } enumModel
                && Enum.GetNames(underlying).Order(StringComparer.Ordinal).SequenceEqual(enumModel.Members.Order(StringComparer.Ordinal));
        if (!fits)
        {
            throw mismatch($"has '{member.Member}' of type {member.ValueType.Name}, but the model says {model.Type}");
        }

        if (model is { IsNullable: true, IsCollection: false } && valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null)
        {
            throw mismatch($"has '{member.Member}' as non-nullable, but the model says it can be null");
        }

        return new ClientProperty(member, model);
    }

    /// <summary>Binds a value type once per binding: every member of the model, nothing else.</summary>
    private static ClientValueType BindValueType(RestModel document, RestValueTypeModel model, ValueBinding binding, Dictionary<ValueBinding, ClientValueType> valueTypes)
    {
        if (valueTypes.TryGetValue(binding, out var existing))
        {
            return existing;
        }

        InvalidOperationException ValueMismatch(string problem) => new($"{binding.ClrType.Name}, the class of the value type '{model.Name}', {problem}.");
        foreach (var extra in binding.Members.Where(member => model.FindProperty(member.Name) is null))
        {
            throw ValueMismatch($"binds '{extra.Name}', which the model doesn't have");
        }

        var properties = model.Properties.Select(property => BindProperty(document, binding.Members, property, valueTypes, ValueMismatch)).ToArray();
        var bound = new ClientValueType(model, binding, properties);
        valueTypes.Add(binding, bound);
        return bound;
    }

    private static ClientNavigation BindNavigation(ClientSet set, SetBinding binding, RestNavigationModel model, Dictionary<string, ClientSet> sets)
    {
        var member = binding.Members.Find(candidate => candidate.Name == model.Name && candidate.Kind is MemberKind.Reference or MemberKind.Collection)
            ?? throw Mismatch(set.Model, set.ClrType, $"has no binding for '{model.Name}'");
        var target = sets[model.Target];
        if (member.ElementType != target.ClrType || (member.Kind == MemberKind.Collection) != model.IsCollection)
        {
            throw Mismatch(set.Model, set.ClrType, $"has the navigation '{member.Member}' of type {member.ValueType.Name}, but the model says {(model.IsCollection ? "a collection of " : "")}{target.ClrType.Name}");
        }

        return new ClientNavigation(member, model, target)
        {
            ForeignKey = model.ForeignKey is [var name] ? set.Properties.Single(property => property.WireName == name) : null,
        };
    }

    private static InvalidOperationException Mismatch(RestSetModel set, Type type, string problem) =>
        new($"{type.Name}, the class of the set '{set.Name}', {problem}.");

    /// <summary>The reflection-based binding of a hand-written class, in the same shape as a generated one.</summary>
    [RequiresUnreferencedCode("Finds properties by reflection.")]
    [RequiresDynamicCode("Compiles getters and creates lists at run time.")]
    private sealed class ReflectedBinding : SetBinding
    {
        private ReflectedBinding(string setName, Type type)
            : base(setName, type, () => Activator.CreateInstance(type)!)
        {
        }

        public static ReflectedBinding For(RestModel document, RestSetModel set, Type type)
        {
            var binding = new ReflectedBinding(set.Name, type);
            foreach (var property in set.Properties)
            {
                var info = FindClrProperty(type, property.Name) ?? throw Mismatch(set, type, $"has no public read-write property for '{property.Name}'");
                if (document.FindValueType(property.Type) is { } valueModel)
                {
                    var valueBinding = ReflectedValueBinding.For(document, valueModel, info.PropertyType);
                    binding.Members.Add(new MemberBinding(property.Name, info.Name, MemberKind.Value, info.PropertyType, null)
                    {
                        Get = Getter(info),
                        Set = info.SetValue,
                        Read = valueBinding.Read,
                        ReadInto = (row, element, location) => info.SetValue(row, valueBinding.Read(element, location)),
                        Value = valueBinding,
                    });
                    continue;
                }

                var elementType = ScalarTypes.GetCollectionElementType(info.PropertyType);
                binding.Members.Add(new MemberBinding(property.Name, info.Name, elementType is null ? MemberKind.Property : MemberKind.Values, info.PropertyType, elementType)
                {
                    Get = Getter(info),
                    Set = info.SetValue,
                    Read = (element, location) => JsonValues.Read(element, info.PropertyType, location),
                    ReadInto = (row, element, location) => info.SetValue(row, JsonValues.Read(element, info.PropertyType, location)),
                });
            }

            foreach (var navigation in set.Navigations)
            {
                var info = FindClrProperty(type, navigation.Name) ?? throw Mismatch(set, type, $"has no public read-write property for '{navigation.Name}'");
                var target = navigation.IsCollection ? ScalarTypes.GetCollectionElementType(info.PropertyType) : info.PropertyType;
                binding.Members.Add(new MemberBinding(navigation.Name, info.Name, navigation.IsCollection ? MemberKind.Collection : MemberKind.Reference, info.PropertyType, target)
                {
                    Get = Getter(info),
                    Set = info.SetValue,
                    NewList = navigation.IsCollection && target is not null ? () => NewList(info.PropertyType, target) : null,
                });
            }

            return binding;
        }

        public static PropertyInfo? FindClrProperty(Type type, string wireName, bool settable = true) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(info => info.GetIndexParameters().Length == 0 && info.GetMethod?.IsPublic == true && (!settable || info.SetMethod?.IsPublic == true)
                    && JsonNamingPolicy.CamelCase.ConvertName(info.Name) == wireName);

        // Compiled once per property: change detection reads every member of every tracked row.
        public static Func<object, object?> Getter(PropertyInfo property)
        {
            var entity = Expression.Parameter(typeof(object), "entity");
            var value = Expression.Property(Expression.Convert(entity, property.DeclaringType!), property);
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(value, typeof(object)), entity).Compile();
        }

        private static IList NewList(Type propertyType, Type elementType) =>
            (IList)Activator.CreateInstance(propertyType.IsInterface ? typeof(List<>).MakeGenericType(elementType) : propertyType)!;
    }

    /// <summary>
    /// The reflection-based binding of a hand-written value class: members by camelCase name (read-only ones too), built
    /// through the public constructor with the most parameters that all match members, the other members set after.
    /// </summary>
    [RequiresUnreferencedCode("Finds properties and constructors by reflection.")]
    [RequiresDynamicCode("Compiles getters at run time.")]
    private sealed class ReflectedValueBinding : ValueBinding
    {
        private readonly ConstructorInfo _constructor;
        private readonly int[] _arguments;
        private readonly (PropertyInfo Property, int Index)[] _setters;

        private ReflectedValueBinding(RestValueTypeModel model, Type type, List<MemberBinding> members, List<PropertyInfo> properties)
            : base(model.Name, type)
        {
            Members.AddRange(members);
            (ConstructorInfo Constructor, int[] Arguments)? best = null;
            foreach (var constructor in type.GetConstructors())
            {
                var arguments = constructor.GetParameters()
                    .Select(parameter => properties.FindIndex(property => string.Equals(property.Name, parameter.Name, StringComparison.OrdinalIgnoreCase) && property.PropertyType == parameter.ParameterType))
                    .ToArray();
                if (!arguments.Contains(-1) && arguments.Distinct().Count() == arguments.Length && (best is null || arguments.Length > best.Value.Arguments.Length))
                {
                    best = (constructor, arguments);
                }
            }

            (_constructor, _arguments) = best ?? throw new InvalidOperationException($"{type.Name}, the class of the value type '{model.Name}', needs a public constructor whose parameters match its members, or a parameterless one.");
            _setters = [.. properties.Select((property, index) => (property, index)).Where(pair => !_arguments.Contains(pair.index))];
            if (_setters.FirstOrDefault(pair => pair.Property.SetMethod?.IsPublic != true).Property is { } readOnly)
            {
                throw new InvalidOperationException($"{type.Name}, the class of the value type '{model.Name}', can't set '{readOnly.Name}': it's neither a constructor parameter nor settable.");
            }
        }

        public static ReflectedValueBinding For(RestModel document, RestValueTypeModel model, Type type)
        {
            var members = new List<MemberBinding>();
            var properties = new List<PropertyInfo>();
            foreach (var property in model.Properties)
            {
                var info = ReflectedBinding.FindClrProperty(type, property.Name, settable: false)
                    ?? throw new InvalidOperationException($"{type.Name}, the class of the value type '{model.Name}', has no public property for '{property.Name}'.");
                var nested = document.FindValueType(property.Type) is { } nestedModel ? For(document, nestedModel, info.PropertyType) : null;
                properties.Add(info);
                members.Add(new MemberBinding(property.Name, info.Name, nested is null ? MemberKind.Property : MemberKind.Value, info.PropertyType, null)
                {
                    Get = ReflectedBinding.Getter(info),
                    Set = static (_, _) => throw new InvalidOperationException("Members of a value are set when it's built."),
                    Read = nested is null ? (element, location) => JsonValues.Read(element, info.PropertyType, location) : nested.Read,
                    Value = nested,
                });
            }

            return new ReflectedValueBinding(model, type, members, properties);
        }

        private protected override object Create(object?[] values)
        {
            var created = _constructor.Invoke([.. _arguments.Select(index => values[index])]);
            foreach (var (property, index) in _setters)
            {
                property.SetValue(created, values[index]);
            }

            return created;
        }
    }
}

/// <summary>A value type bound to its client class: its members in model order, and how to write and compare values.</summary>
internal sealed class ClientValueType(RestValueTypeModel model, ValueBinding binding, ClientProperty[] properties)
{
    public RestValueTypeModel Model { get; } = model;

    public string Name => Model.Name;

    public Type ClrType => binding.ClrType;

    /// <summary>Gets the members, in model order.</summary>
    public ClientProperty[] Properties { get; } = properties;

    public ClientProperty? FindProperty(string wireName) => Array.Find(Properties, property => property.WireName == wireName);

    /// <summary>Writes a value as the JSON object batches send: every member, in model order.</summary>
    public void Write(Utf8JsonWriter writer, object? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var property in Properties)
        {
            writer.WritePropertyName(property.WireName);
            if (property.ValueType is { } nested)
            {
                nested.Write(writer, property.Get(value));
            }
            else
            {
                JsonValues.Write(writer, property.Get(value));
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Copies a value's members (nested values too), so change detection sees a value changed in place as well as a
    /// replaced one.
    /// </summary>
    public object?[]? Snapshot(object? value) =>
        value is null ? null : [.. Properties.Select(property => property.ValueType is { } nested ? nested.Snapshot(property.Get(value)) : property.Get(value))];

    public bool Same(object? value, object? snapshot) => SameValues(Snapshot(value), snapshot as object?[]);

    private static bool SameValues(object?[]? left, object?[]? right) =>
        left is null || right is null
            ? left is null && right is null
            : left.Length == right.Length && left.Zip(right).All(pair => pair.First is object?[] nested ? SameValues(nested, pair.Second as object?[]) : Equals(pair.First, pair.Second));

    public override string ToString() => Name;
}

/// <summary>A set bound to its client class.</summary>
internal sealed class ClientSet(RestSetModel model, Type clrType, Func<object> create, ClientProperty[] properties)
{
    private readonly Dictionary<string, ClientProperty> _properties = properties.ToDictionary(property => property.WireName, StringComparer.Ordinal);
    private Dictionary<string, ClientNavigation> _navigations = [];

    public RestSetModel Model { get; } = model;

    public Type ClrType { get; } = clrType;

    /// <summary>Gets a factory of empty rows.</summary>
    public Func<object> Create { get; } = create;

    public string SetName => Model.Name;

    public RestPaginationModel Pagination => Model.Pagination;

    public ClientProperty[] Properties { get; } = properties;

    public ClientNavigation[] Navigations { get; private set; } = [];

    public ClientProperty[] Key { get; } = [.. model.Key.Select(name => properties.Single(property => property.WireName == name))];

    public ClientProperty? ConcurrencyToken { get; } = properties.SingleOrDefault(property => property.IsConcurrencyToken);

    public ClientProperty? FindProperty(string wireName) => _properties.GetValueOrDefault(wireName);

    /// <summary>
    /// Finds the member a JSON property names, comparing UTF-8 without allocating its name. Rows usually list members
    /// in model order, so the search starts at <paramref name="hint"/> (one past the previous match).
    /// </summary>
    /// <returns>The index into <see cref="Members"/>, or -1 for an unknown key.</returns>
    public int Match(JsonProperty member, int hint)
    {
        for (var offset = 0; offset < Members.Length; offset++)
        {
            var index = (hint + offset) % Members.Length;
            if (member.NameEquals(Members[index].Utf8Name))
            {
                return index;
            }
        }

        return -1;
    }

    public ClientNavigation? FindNavigation(string wireName) => _navigations.GetValueOrDefault(wireName);

    /// <summary>
    /// The dotted CLR path of a wire member path, such as <c>address/city</c> → <c>Address.City</c>, or
    /// <see langword="null"/> when the model has no such member.
    /// </summary>
    public string? MemberPath(string wirePath)
    {
        var segments = wirePath.Split('/');
        var property = FindProperty(segments[0]);
        var path = property?.Name;
        foreach (var segment in segments.AsSpan(1))
        {
            property = property?.ValueType?.FindProperty(segment);
            path = property is null ? null : $"{path}.{property.Name}";
        }

        return path;
    }

    public bool Allows(WriteOperations operations) => Model.Allows(operations);

    /// <summary>Gets the properties, then the navigations, with their UTF-8 wire names.</summary>
    public (byte[] Utf8Name, ClientProperty? Property, ClientNavigation? Navigation)[] Members { get; private set; } = [];

    public void Initialize(ClientNavigation[] navigations)
    {
        Navigations = navigations;
        _navigations = navigations.ToDictionary(navigation => navigation.WireName, StringComparer.Ordinal);
        Members =
        [
            .. Properties.Select(property => (Encoding.UTF8.GetBytes(property.WireName), (ClientProperty?)property, (ClientNavigation?)null)),
            .. navigations.Select(navigation => (Encoding.UTF8.GetBytes(navigation.WireName), (ClientProperty?)null, (ClientNavigation?)navigation)),
        ];
    }

    public override string ToString() => ClrType.Name;
}

/// <summary>A scalar, primitive-collection or value-type property (of a set or of a value type) bound to its client property.</summary>
internal sealed class ClientProperty(MemberBinding binding, RestPropertyModel model)
{
    /// <summary>Gets the CLR property name.</summary>
    public string Name => binding.Member;

    public string WireName => model.Name;

    public Type ClrType => binding.ValueType;

    public Type? ElementType => binding.Kind == MemberKind.Values ? binding.ElementType : null;

    public bool IsCollection => ElementType is not null;

    /// <summary>Gets the value type of a property holding one.</summary>
    public ClientValueType? ValueType { get; init; }

    public bool IsNullable => model.IsNullable;

    public bool IsKey { get; private set; }

    public bool IsConcurrencyToken { get; private set; }

    /// <summary>Marks a set's key and concurrency token, once the set is bound.</summary>
    public ClientProperty OfSet(RestSetModel set)
    {
        IsKey = set.Key.Contains(model.Name);
        IsConcurrencyToken = set.ConcurrencyToken == model.Name;
        return this;
    }

    public bool IsWritable => model.IsWritable;

    public bool IsSortable => model.IsSortable;

    public bool IsFilterable => model.IsFilterable;

    public bool IsGroupable => model.IsGroupable;

    public bool IsAggregatable => model.IsAggregatable;

    /// <summary>Gets the built-in validation rules from the model, or <see langword="null"/>.</summary>
    public RestPropertyRules? Rules => model.Rules;

    public Func<object, object?> Get => binding.Get;

    public Action<object, object?> Set => binding.Set;

    /// <summary>Encodes a value of the property as a batch sends it (a value type as an object of its members).</summary>
    public JsonElement ToElement(object? value)
    {
        if (ValueType is not { } valueType)
        {
            return JsonValues.ToElement(value);
        }

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            valueType.Write(writer, value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Gets the reader of the property's JSON value.</summary>
    public Func<JsonElement, string, object?> Read => binding.Read!;

    /// <summary>Gets the reader that sets the property's JSON value on a row (typed for generated bindings).</summary>
    public Action<object, JsonElement, string> ReadInto => binding.ReadInto!;

    public override string ToString() => $"{model.Name} ({Name})";
}

/// <summary>A navigation bound to its client property.</summary>
internal sealed class ClientNavigation(MemberBinding binding, RestNavigationModel model, ClientSet target)
{
    public string Name => binding.Member;

    public string WireName => model.Name;

    public ClientSet Target { get; } = target;

    public bool IsCollection => model.IsCollection;

    public bool IsNullable => model.IsNullable;

    public ClientProperty? ForeignKey { get; init; }

    public Func<object, object?> Get => binding.Get;

    public Action<object, object?> Set => binding.Set;

    /// <summary>Gets a factory of empty lists, for a collection navigation.</summary>
    public Func<IList> NewList => binding.NewList!;

    public override string ToString() => $"{model.Name} ({Name})";
}

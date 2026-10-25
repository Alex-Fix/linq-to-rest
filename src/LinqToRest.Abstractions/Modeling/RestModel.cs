using System.Text.RegularExpressions;

namespace LinqToRest.Modeling;

/// <summary>
/// The model document: the sets a server exposes, their members, types and rules, without any CLR or server
/// details. Servers produce it from their set configurations; client generators read it. Immutable and validated
/// on construction.
/// </summary>
public sealed partial class RestModel
{
    /// <summary>The model document version this library reads and writes.</summary>
    public const int Version = 1;

    /// <summary>How many levels of value types may nest: a value type, one inside it, and one inside that.</summary>
    public const int MaxValueTypeDepth = 3;

    // Scalar types a range rule applies to.
    private static readonly HashSet<Type> RangeTypes =
    [
        typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal),
        typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan),
    ];

    private readonly Dictionary<string, RestSetModel> _sets;
    private readonly Dictionary<string, RestEnumModel> _enums;
    private readonly Dictionary<string, RestValueTypeModel> _valueTypes;

    /// <summary>Creates and validates a model.</summary>
    /// <param name="sets">The sets.</param>
    /// <param name="enums">The enums the properties use.</param>
    /// <param name="valueTypes">The value types the properties use.</param>
    /// <exception cref="ArgumentException">The model is inconsistent; the message says why.</exception>
    public RestModel(IEnumerable<RestSetModel> sets, IEnumerable<RestEnumModel>? enums = null, IEnumerable<RestValueTypeModel>? valueTypes = null)
    {
        ArgumentNullException.ThrowIfNull(sets);
        Sets = [.. sets];
        Enums = [.. enums ?? []];
        ValueTypes = [.. valueTypes ?? []];
        _enums = new Dictionary<string, RestEnumModel>(StringComparer.Ordinal);
        _valueTypes = new Dictionary<string, RestValueTypeModel>(StringComparer.Ordinal);
        _sets = new Dictionary<string, RestSetModel>(StringComparer.Ordinal);
        Validate();
    }

    /// <summary>Gets the sets, in document order.</summary>
    public IReadOnlyList<RestSetModel> Sets { get; }

    /// <summary>Gets the enums, in document order.</summary>
    public IReadOnlyList<RestEnumModel> Enums { get; }

    /// <summary>Gets the value types, in document order.</summary>
    public IReadOnlyList<RestValueTypeModel> ValueTypes { get; }

    /// <summary>Finds a set by name.</summary>
    /// <param name="name">The set name.</param>
    /// <returns>The set, or <see langword="null"/>.</returns>
    public RestSetModel? FindSet(string name) => _sets.GetValueOrDefault(name);

    /// <summary>Finds an enum by name.</summary>
    /// <param name="name">The enum name.</param>
    /// <returns>The enum, or <see langword="null"/>.</returns>
    public RestEnumModel? FindEnum(string name) => _enums.GetValueOrDefault(name);

    /// <summary>Finds a value type by name, for example the <see cref="RestPropertyModel.Type"/> of a property.</summary>
    /// <param name="name">The type name.</param>
    /// <returns>The value type, or <see langword="null"/>.</returns>
    public RestValueTypeModel? FindValueType(string name) => _valueTypes.GetValueOrDefault(name);

    /// <summary>Reads a model document.</summary>
    /// <param name="json">The JSON document.</param>
    /// <returns>The validated model.</returns>
    /// <exception cref="FormatException">The document is invalid, unsupported or too large; the message says why.</exception>
    public static RestModel Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return RestModelJson.Read(System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Reads a UTF-8 model document.</summary>
    /// <param name="utf8Json">The UTF-8 JSON document.</param>
    /// <returns>The validated model.</returns>
    /// <exception cref="FormatException">The document is invalid, unsupported or too large; the message says why.</exception>
    public static RestModel Parse(ReadOnlySpan<byte> utf8Json) => RestModelJson.Read(utf8Json);

    /// <summary>Writes the model document. Output is deterministic, so checked-in copies diff cleanly.</summary>
    /// <param name="indented">Whether to indent the output.</param>
    /// <returns>The JSON document.</returns>
    public string ToJson(bool indented = true) => System.Text.Encoding.UTF8.GetString(ToUtf8Json(indented));

    /// <summary>Writes the model document as UTF-8. Output is deterministic, so checked-in copies diff cleanly.</summary>
    /// <param name="indented">Whether to indent the output.</param>
    /// <returns>The UTF-8 JSON document.</returns>
    public byte[] ToUtf8Json(bool indented = true) => RestModelJson.Write(this, indented);

    private void Validate()
    {
        if (Sets.Count == 0)
        {
            throw Invalid("The model has no sets.");
        }

        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in Enums)
        {
            ArgumentNullException.ThrowIfNull(model, nameof(Enums));
            if (!TypeNamePattern().IsMatch(model.Name))
            {
                throw Invalid($"The enum name '{model.Name}' is invalid. Use an uppercase letter followed by letters or digits, at most 64 characters.");
            }

            if (!typeNames.Add(model.Name))
            {
                throw Invalid($"The type name '{model.Name}' is used twice.");
            }

            if (model.Members.Count == 0)
            {
                throw Invalid($"The enum '{model.Name}' has no members.");
            }

            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in model.Members)
            {
                if (!EnumMemberPattern().IsMatch(member) || !members.Add(member))
                {
                    throw Invalid($"The enum '{model.Name}' has an invalid or duplicate member '{member}'.");
                }
            }

            _enums.Add(model.Name, model);
        }

        foreach (var model in ValueTypes)
        {
            ArgumentNullException.ThrowIfNull(model, nameof(ValueTypes));
            if (!TypeNamePattern().IsMatch(model.Name))
            {
                throw Invalid($"The value type name '{model.Name}' is invalid. Use an uppercase letter followed by letters or digits, at most 64 characters.");
            }

            if (!typeNames.Add(model.Name))
            {
                throw Invalid($"The type name '{model.Name}' is used twice.");
            }

            _valueTypes.Add(model.Name, model);
        }

        foreach (var set in Sets)
        {
            ArgumentNullException.ThrowIfNull(set, nameof(Sets));
            if (!SetNamePattern().IsMatch(set.Name))
            {
                throw Invalid($"The set name '{set.Name}' is invalid. Use a lowercase letter followed by letters or digits, at most 64 characters.");
            }

            if (!_sets.TryAdd(set.Name, set))
            {
                throw Invalid($"The set name '{set.Name}' is used twice.");
            }

            if (!TypeNamePattern().IsMatch(set.TypeName))
            {
                throw Invalid($"The set '{set.Name}' has an invalid type name '{set.TypeName}'. Use an uppercase letter followed by letters or digits, at most 64 characters.");
            }

            if (!typeNames.Add(set.TypeName))
            {
                throw Invalid($"The type name '{set.TypeName}' is used twice.");
            }
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in Sets)
        {
            ValidateMembers(set, used);
        }

        foreach (var model in ValueTypes)
        {
            ValidateValueType(model, used);
        }

        foreach (var set in Sets)
        {
            set.Index();
        }

        foreach (var model in ValueTypes)
        {
            model.Index();
        }

        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var model in ValueTypes)
        {
            ValidateNesting(model, [], depths);
        }

        foreach (var set in Sets)
        {
            ValidateKeys(set);
        }

        foreach (var model in Enums.Where(model => !used.Contains(model.Name)))
        {
            throw Invalid($"The enum '{model.Name}' isn't used by any property.");
        }

        foreach (var model in ValueTypes.Where(model => !used.Contains(model.Name)))
        {
            throw Invalid($"The value type '{model.Name}' isn't used by any property.");
        }
    }

    private void ValidateMembers(RestSetModel set, HashSet<string> usedTypes)
    {
        var pagination = set.Pagination ?? throw Invalid($"The set '{set.Name}' has no pagination.");
        if (pagination.Modes == PaginationModes.None || (pagination.Modes & ~PaginationModes.All) != 0)
        {
            throw Invalid($"The set '{set.Name}' must allow at least one valid pagination mode.");
        }

        if (pagination.MaxPageSize < 1 || pagination.DefaultPageSize < 1 || pagination.DefaultPageSize > pagination.MaxPageSize || pagination.MaxSkip < 0)
        {
            throw Invalid($"The set '{set.Name}' needs 1 <= defaultPageSize <= maxPageSize and maxSkip >= 0.");
        }

        if ((set.Writes & ~WriteOperations.All) != 0)
        {
            throw Invalid($"The set '{set.Name}' has unknown write operations.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var writable = set.Allows(WriteOperations.Insert) || set.Allows(WriteOperations.Update);
        foreach (var property in set.Properties)
        {
            ArgumentNullException.ThrowIfNull(property, nameof(set.Properties));
            if (!MemberNamePattern().IsMatch(property.Name) || !names.Add(property.Name))
            {
                throw Invalid($"The set '{set.Name}' has an invalid or duplicate member name '{property.Name}'.");
            }

            ValidateProperty(set.Name, property, inValueType: false, writable, usedTypes);
        }

        foreach (var navigation in set.Navigations)
        {
            ArgumentNullException.ThrowIfNull(navigation, nameof(set.Navigations));
            if (!MemberNamePattern().IsMatch(navigation.Name) || !names.Add(navigation.Name))
            {
                throw Invalid($"The set '{set.Name}' has an invalid or duplicate member name '{navigation.Name}'.");
            }

            if (!_sets.ContainsKey(navigation.Target))
            {
                throw Invalid($"The navigation '{set.Name}.{navigation.Name}' targets the unknown set '{navigation.Target}'.");
            }

            if (navigation.IsCollection && (navigation.IsNullable || navigation.ForeignKey.Count > 0))
            {
                throw Invalid($"The collection navigation '{set.Name}.{navigation.Name}' can't be nullable or have a foreign key.");
            }
        }
    }

    private void ValidateValueType(RestValueTypeModel model, HashSet<string> usedTypes)
    {
        if (model.Properties.Count == 0)
        {
            throw Invalid($"The value type '{model.Name}' has no members.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in model.Properties)
        {
            ArgumentNullException.ThrowIfNull(property, nameof(model.Properties));
            if (!MemberNamePattern().IsMatch(property.Name) || !names.Add(property.Name))
            {
                throw Invalid($"The value type '{model.Name}' has an invalid or duplicate member name '{property.Name}'.");
            }

            if (property.IsWritable)
            {
                throw Invalid($"The member '{model.Name}.{property.Name}' is writable, but writability belongs to the property that uses the value type.");
            }

            ValidateProperty(model.Name, property, inValueType: true, canWrite: false, usedTypes);
        }
    }

    private void ValidateProperty(string owner, RestPropertyModel property, bool inValueType, bool canWrite, HashSet<string> usedTypes)
    {
        var name = $"{owner}.{property.Name}";
        var scalar = ScalarTypes.FromName(property.Type);
        if (scalar is null)
        {
            if (_valueTypes.ContainsKey(property.Type))
            {
                if (property.IsCollection || property.IsSortable || property.IsGroupable || property.IsAggregatable || property.Rules is not null)
                {
                    throw Invalid($"The property '{name}' has the value type '{property.Type}', so it can only be nullable, writable or not filterable.");
                }
            }
            else if (!_enums.ContainsKey(property.Type))
            {
                throw Invalid($"The property '{name}' has the unknown type '{property.Type}'.");
            }

            usedTypes.Add(property.Type);
        }

        if (property.IsCollection && scalar == typeof(byte[]))
        {
            throw Invalid($"The property '{name}' is a collection of binary values, which isn't supported.");
        }

        if (property.IsSortable && (property.IsCollection || scalar == typeof(byte[])))
        {
            throw Invalid($"The property '{name}' is sortable, but collections and binary values can't be sorted.");
        }

        if (property.IsWritable && !canWrite)
        {
            throw Invalid($"The property '{name}' is writable, but the set allows neither inserts nor updates.");
        }

        if (property.IsGroupable && (property.IsCollection || scalar == typeof(byte[])))
        {
            throw Invalid($"The property '{name}' is groupable, but collections and binary values can't be group keys.");
        }

        if (property.IsAggregatable && (property.IsCollection || scalar is null || !RangeTypes.Contains(scalar)))
        {
            throw Invalid($"The property '{name}' is aggregatable, but only numbers, dates and times can be summed, averaged or compared.");
        }

        if (property.Rules is { } rules)
        {
            // A value type's members are written with the value, by whichever property uses it.
            if (!property.IsWritable && !inValueType)
            {
                throw Invalid($"The property '{name}' has rules but isn't writable, so they could never apply.");
            }

            ValidateRules(name, property, scalar, rules);
        }
    }

    // Value types nest at most MaxValueTypeDepth levels and never contain themselves. Depths are memoized and the walk
    // stops past the limit, so wide documents stay linear.
    private int ValidateNesting(RestValueTypeModel model, List<string> path, Dictionary<string, int> depths)
    {
        if (depths.TryGetValue(model.Name, out var known))
        {
            return known;
        }

        if (path.Contains(model.Name))
        {
            throw Invalid($"The value type '{model.Name}' contains itself.");
        }

        if (path.Count == MaxValueTypeDepth)
        {
            throw Invalid($"The value type '{path[0]}' nests value types more than {MaxValueTypeDepth} levels deep.");
        }

        path.Add(model.Name);
        var depth = 1;
        foreach (var property in model.Properties)
        {
            if (_valueTypes.GetValueOrDefault(property.Type) is { } nested)
            {
                depth = Math.Max(depth, 1 + ValidateNesting(nested, path, depths));
            }
        }

        path.RemoveAt(path.Count - 1);
        if (path.Count + depth > MaxValueTypeDepth)
        {
            throw Invalid($"The value type '{path.FirstOrDefault() ?? model.Name}' nests value types more than {MaxValueTypeDepth} levels deep.");
        }

        depths.Add(model.Name, depth);
        return depth;
    }

    private static void ValidateRules(string name, RestPropertyModel property, Type? scalar, RestPropertyRules rules)
    {
        var isString = scalar == typeof(string) && !property.IsCollection;
        if ((rules.MinLength is not null || rules.MaxLength is not null) && !isString && !property.IsCollection)
        {
            throw Invalid($"The property '{name}' has a length rule, but only strings and collections have a length.");
        }

        if (rules.MinLength < 0 || rules.MaxLength < 0 || rules.MinLength > rules.MaxLength)
        {
            throw Invalid($"The property '{name}' needs 0 <= minLength <= maxLength.");
        }

        if (rules.Min is not null || rules.Max is not null)
        {
            if (property.IsCollection || scalar is null || !RangeTypes.Contains(scalar))
            {
                throw Invalid($"The property '{name}' has a range, but only numbers, dates and times have one.");
            }

            if (!IsBound(rules.Min, scalar) || !IsBound(rules.Max, scalar)
                || (rules is { Min: IComparable min, Max: { } max } && min.CompareTo(max) > 0))
            {
                throw Invalid($"The property '{name}' needs min <= max, both finite values of its type.");
            }
        }

        if (rules.Pattern is { } pattern)
        {
            if (!isString)
            {
                throw Invalid($"The property '{name}' has a pattern, but only strings can have one.");
            }

            if (pattern.Length is 0 or > RestPropertyRules.MaxPatternLength)
            {
                throw Invalid($"The property '{name}' has a pattern that is empty or longer than {RestPropertyRules.MaxPatternLength} characters.");
            }

            try
            {
                RestPropertyRules.CreateRegex(pattern);
            }
            catch (ArgumentException exception)
            {
                throw Invalid($"The property '{name}' has a pattern that doesn't mean the same in .NET and JavaScript: {exception.Message}");
            }
        }
    }

    private static bool IsBound(object? bound, Type type) =>
        bound is null || (bound.GetType() == type && bound is not (double.NaN or double.PositiveInfinity or double.NegativeInfinity)
            && bound is not (float.NaN or float.PositiveInfinity or float.NegativeInfinity));

    private void ValidateKeys(RestSetModel set)
    {
        if (set.Key.Count == 0)
        {
            throw Invalid($"The set '{set.Name}' has no key.");
        }

        foreach (var name in set.Key)
        {
            var key = set.FindProperty(name);
            if (key is null || key.IsNullable || key.IsCollection || key.IsWritable || !key.IsSortable || key.Type == "bytes")
            {
                throw Invalid($"The key '{set.Name}.{name}' must be a non-nullable, non-writable, sortable scalar property.");
            }
        }

        if (set.Key.Distinct(StringComparer.Ordinal).Count() != set.Key.Count)
        {
            throw Invalid($"The set '{set.Name}' lists a key property twice.");
        }

        if (set.ConcurrencyToken is { } tokenName)
        {
            var token = set.FindProperty(tokenName);
            if (token is null || token.IsCollection || token.IsWritable || set.Key.Contains(tokenName) || _valueTypes.ContainsKey(token.Type))
            {
                throw Invalid($"The concurrency token '{set.Name}.{tokenName}' must be a non-writable, non-key scalar property.");
            }
        }

        foreach (var navigation in set.Navigations.Where(navigation => navigation.ForeignKey.Count > 0))
        {
            var targetKey = _sets[navigation.Target].Key;
            if (navigation.ForeignKey.Count != targetKey.Count)
            {
                throw Invalid($"The navigation '{set.Name}.{navigation.Name}' has a foreign key that doesn't match the key of '{navigation.Target}'.");
            }

            for (var index = 0; index < targetKey.Count; index++)
            {
                var foreignKey = set.FindProperty(navigation.ForeignKey[index]);
                if (foreignKey is null || foreignKey.IsCollection || foreignKey.Type != _sets[navigation.Target].FindProperty(targetKey[index])!.Type)
                {
                    throw Invalid($"The navigation '{set.Name}.{navigation.Name}' has a foreign key that doesn't match the key of '{navigation.Target}'.");
                }
            }
        }
    }

    private static ArgumentException Invalid(string message) => new(message);

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SetNamePattern();

    [GeneratedRegex("^[a-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex MemberNamePattern();

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnumMemberPattern();
}

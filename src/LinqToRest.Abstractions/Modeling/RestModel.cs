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

    // Scalar types a range rule applies to.
    private static readonly HashSet<Type> RangeTypes =
    [
        typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal),
        typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan),
    ];

    private readonly Dictionary<string, RestSetModel> _sets;
    private readonly Dictionary<string, RestEnumModel> _enums;

    /// <summary>Creates and validates a model.</summary>
    /// <param name="sets">The sets.</param>
    /// <param name="enums">The enums the sets' properties use.</param>
    /// <exception cref="ArgumentException">The model is inconsistent; the message says why.</exception>
    public RestModel(IEnumerable<RestSetModel> sets, IEnumerable<RestEnumModel>? enums = null)
    {
        ArgumentNullException.ThrowIfNull(sets);
        Sets = [.. sets];
        Enums = [.. enums ?? []];
        _enums = new Dictionary<string, RestEnumModel>(StringComparer.Ordinal);
        _sets = new Dictionary<string, RestSetModel>(StringComparer.Ordinal);
        Validate();
    }

    /// <summary>Gets the sets, in document order.</summary>
    public IReadOnlyList<RestSetModel> Sets { get; }

    /// <summary>Gets the enums, in document order.</summary>
    public IReadOnlyList<RestEnumModel> Enums { get; }

    /// <summary>Finds a set by name.</summary>
    /// <param name="name">The set name.</param>
    /// <returns>The set, or <see langword="null"/>.</returns>
    public RestSetModel? FindSet(string name) => _sets.GetValueOrDefault(name);

    /// <summary>Finds an enum by name.</summary>
    /// <param name="name">The enum name.</param>
    /// <returns>The enum, or <see langword="null"/>.</returns>
    public RestEnumModel? FindEnum(string name) => _enums.GetValueOrDefault(name);

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

        foreach (var set in Sets)
        {
            set.Index();
        }

        foreach (var set in Sets)
        {
            ValidateKeys(set);
        }

        foreach (var model in Enums.Where(model => !used.Contains(model.Name)))
        {
            throw Invalid($"The enum '{model.Name}' isn't used by any property.");
        }
    }

    private void ValidateMembers(RestSetModel set, HashSet<string> usedEnums)
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
        foreach (var property in set.Properties)
        {
            ArgumentNullException.ThrowIfNull(property, nameof(set.Properties));
            if (!MemberNamePattern().IsMatch(property.Name) || !names.Add(property.Name))
            {
                throw Invalid($"The set '{set.Name}' has an invalid or duplicate member name '{property.Name}'.");
            }

            var scalar = ScalarTypes.FromName(property.Type);
            if (scalar is null)
            {
                if (!_enums.ContainsKey(property.Type))
                {
                    throw Invalid($"The property '{set.Name}.{property.Name}' has the unknown type '{property.Type}'.");
                }

                usedEnums.Add(property.Type);
            }

            if (property.IsCollection && scalar == typeof(byte[]))
            {
                throw Invalid($"The property '{set.Name}.{property.Name}' is a collection of binary values, which isn't supported.");
            }

            if (property.IsSortable && (property.IsCollection || scalar == typeof(byte[])))
            {
                throw Invalid($"The property '{set.Name}.{property.Name}' is sortable, but collections and binary values can't be sorted.");
            }

            if (property.IsWritable && !set.Allows(WriteOperations.Insert) && !set.Allows(WriteOperations.Update))
            {
                throw Invalid($"The property '{set.Name}.{property.Name}' is writable, but the set allows neither inserts nor updates.");
            }

            if (property.IsGroupable && (property.IsCollection || scalar == typeof(byte[])))
            {
                throw Invalid($"The property '{set.Name}.{property.Name}' is groupable, but collections and binary values can't be group keys.");
            }

            if (property.IsAggregatable && (property.IsCollection || scalar is null || !RangeTypes.Contains(scalar)))
            {
                throw Invalid($"The property '{set.Name}.{property.Name}' is aggregatable, but only numbers, dates and times can be summed, averaged or compared.");
            }

            if (property.Rules is { } rules)
            {
                ValidateRules(set, property, scalar, rules);
            }
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

    private static void ValidateRules(RestSetModel set, RestPropertyModel property, Type? scalar, RestPropertyRules rules)
    {
        var name = $"{set.Name}.{property.Name}";
        if (!property.IsWritable)
        {
            throw Invalid($"The property '{name}' has rules but isn't writable, so they could never apply.");
        }

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
            if (token is null || token.IsCollection || token.IsWritable || set.Key.Contains(tokenName))
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

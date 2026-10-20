namespace LinqToRest.Modeling;

/// <summary>The CLR types LinqToRest accepts for DTO scalar properties and literals.</summary>
public static class ScalarTypes
{
    private static readonly HashSet<Type> Supported =
    [
        typeof(bool), typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double),
        typeof(decimal), typeof(string), typeof(Guid), typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly),
        typeof(TimeOnly), typeof(TimeSpan), typeof(byte[]),
    ];

    private static readonly Dictionary<Type, string> NamesByType = new()
    {
        [typeof(bool)] = "bool",
        [typeof(byte)] = "uint8",
        [typeof(short)] = "int16",
        [typeof(int)] = "int32",
        [typeof(long)] = "int64",
        [typeof(float)] = "float32",
        [typeof(double)] = "float64",
        [typeof(decimal)] = "decimal",
        [typeof(string)] = "string",
        [typeof(Guid)] = "guid",
        [typeof(DateTime)] = "dateTime",
        [typeof(DateTimeOffset)] = "dateTimeOffset",
        [typeof(DateOnly)] = "dateOnly",
        [typeof(TimeOnly)] = "timeOnly",
        [typeof(TimeSpan)] = "timeSpan",
        [typeof(byte[])] = "bytes",
    };

    private static readonly Dictionary<string, Type> TypesByName =
        NamesByType.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private static readonly HashSet<Type> Numeric =
    [
        typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal),
    ];

    /// <summary>Returns whether <paramref name="type"/> (or its underlying type when nullable) is a supported scalar.</summary>
    /// <param name="type">The type to check.</param>
    /// <returns><see langword="true"/> for supported scalars, including enums.</returns>
    public static bool IsScalar(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return Supported.Contains(underlying) || underlying.IsEnum;
    }

    /// <summary>Returns whether <paramref name="type"/> (or its underlying type when nullable) is numeric.</summary>
    /// <param name="type">The type to check.</param>
    /// <returns><see langword="true"/> for numeric types.</returns>
    public static bool IsNumeric(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return Numeric.Contains(Nullable.GetUnderlyingType(type) ?? type);
    }

    /// <summary>
    /// Returns the element type when <paramref name="type"/> is a collection LinqToRest can materialize:
    /// an array, <see cref="List{T}"/>, or an interface implemented by <see cref="List{T}"/>.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>The element type, or <see langword="null"/>.</returns>
    public static Type? GetCollectionElementType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetArrayRank() == 1 ? type.GetElementType() : null;
        }

        if (!type.IsGenericType)
        {
            return null;
        }

        var element = type.GetGenericArguments()[0];
        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(List<>))
        {
            return element;
        }

        return type.IsInterface && type.GetGenericArguments().Length == 1 && type.IsAssignableFrom(typeof(List<>).MakeGenericType(element))
            ? element
            : null;
    }

    /// <summary>
    /// Returns the model-document name of a supported non-enum scalar (for example <c>int32</c> for
    /// <see cref="int"/>), ignoring nullability.
    /// </summary>
    /// <param name="type">The scalar type.</param>
    /// <returns>The name, or <see langword="null"/> for enums and unsupported types.</returns>
    public static string? GetName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return NamesByType.GetValueOrDefault(Nullable.GetUnderlyingType(type) ?? type);
    }

    /// <summary>Returns the CLR type for a model-document scalar name (for example <see cref="int"/> for <c>int32</c>).</summary>
    /// <param name="name">The scalar name.</param>
    /// <returns>The type, or <see langword="null"/> when <paramref name="name"/> isn't a scalar name.</returns>
    public static Type? FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return TypesByName.GetValueOrDefault(name);
    }
}

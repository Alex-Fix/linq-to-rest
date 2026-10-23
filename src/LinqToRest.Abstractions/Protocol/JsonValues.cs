using System.Buffers;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LinqToRest.Modeling;

namespace LinqToRest.Protocol;

/// <summary>
/// Encodes and decodes scalar and primitive-collection values exactly as they appear on the wire: numbers as JSON
/// numbers, enums by name, dates in ISO 8601 (a <see cref="DateTime"/> with an offset is read as its UTC instant),
/// <see cref="TimeSpan"/> in the constant ("c") format, binary as base64.
/// </summary>
public static class JsonValues
{
    /// <summary>Writes a scalar, enum, primitive collection or <see langword="null"/>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException">The value's type isn't supported.</exception>
    public static void Write(Utf8JsonWriter writer, object? value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case bool v: writer.WriteBooleanValue(v); break;
            case byte v: writer.WriteNumberValue(v); break;
            case short v: writer.WriteNumberValue(v); break;
            case int v: writer.WriteNumberValue(v); break;
            case long v: writer.WriteNumberValue(v); break;
            case float v: writer.WriteNumberValue(v); break;
            case double v: writer.WriteNumberValue(v); break;
            case decimal v: writer.WriteNumberValue(v); break;
            case string v: writer.WriteStringValue(v); break;
            case Guid v: writer.WriteStringValue(v); break;
            case DateTime v: writer.WriteStringValue(v); break;
            case DateTimeOffset v: writer.WriteStringValue(v); break;
            case DateOnly v: writer.WriteStringValue(v.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeOnly v: writer.WriteStringValue(v.ToString("O", CultureInfo.InvariantCulture)); break;
            case TimeSpan v: writer.WriteStringValue(v.ToString("c", CultureInfo.InvariantCulture)); break;
            case byte[] v: writer.WriteBase64StringValue(v); break;
            case Enum v: writer.WriteStringValue(v.ToString()); break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;
            default: throw new ArgumentException($"Values of type {value.GetType().Name} aren't supported.", nameof(value));
        }
    }

    /// <summary>Encodes a value as a standalone <see cref="JsonElement"/>.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The element.</returns>
    public static JsonElement ToElement(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Decodes a value into <paramref name="type"/>, strictly (no string/number coercion, enums by name only).</summary>
    /// <param name="element">The JSON value.</param>
    /// <param name="type">A supported scalar, enum, nullable or primitive-collection type.</param>
    /// <param name="location">The JSON pointer reported on failure.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ProtocolException">The value doesn't fit <paramref name="type"/> (code <see cref="ErrorCodes.TypeMismatch"/>).</exception>
    [RequiresDynamicCode("Creates a List<T> or array for a runtime element type. On trimmed or AOT paths use ReadScalar or ReadList<TElement>.")]
    public static object? Read(JsonElement element, Type type, string location)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (element.ValueKind != JsonValueKind.Null && ScalarTypes.GetCollectionElementType(type) is { } elementType)
        {
            return ReadCollection(element, type, elementType, location);
        }

        return ReadScalar(element, type, location);
    }

    /// <summary>Decodes a scalar, enum or nullable value into <paramref name="type"/>, strictly. Safe for trimming and AOT.</summary>
    /// <param name="element">The JSON value.</param>
    /// <param name="type">A supported scalar, enum or nullable type (not a collection).</param>
    /// <param name="location">The JSON pointer reported on failure.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ProtocolException">The value doesn't fit <paramref name="type"/> (code <see cref="ErrorCodes.TypeMismatch"/>).</exception>
    public static object? ReadScalar(JsonElement element, Type type, string location)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (element.ValueKind == JsonValueKind.Null)
        {
            return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null ? null : throw Mismatch(location);
        }

        var target = Nullable.GetUnderlyingType(type) ?? type;
        return TryReadScalar(element, target, out var value) ? value : throw Mismatch(location);
    }

    /// <summary>
    /// Decodes a scalar, enum or nullable value into <typeparamref name="T"/>, strictly, like
    /// <see cref="ReadScalar(JsonElement, Type, string)"/>; common value types are read without boxing. Safe for
    /// trimming and AOT.
    /// </summary>
    /// <param name="element">The JSON value.</param>
    /// <param name="location">The JSON pointer reported on failure.</param>
    /// <typeparam name="T">A supported scalar, enum or nullable type (not a collection).</typeparam>
    /// <returns>The value.</returns>
    /// <exception cref="ProtocolException">The value doesn't fit <typeparamref name="T"/> (code <see cref="ErrorCodes.TypeMismatch"/>).</exception>
    public static T ReadScalar<T>(JsonElement element, string location)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            // Null for reference and nullable types only (their default is null).
            return default(T) is null ? default! : throw Mismatch(location);
        }

        // typeof(T) tests are constants per instantiation, so each T compiles to its own branch.
        var kind = element.ValueKind;
        if (typeof(T) == typeof(int) || typeof(T) == typeof(int?))
        {
            return kind == JsonValueKind.Number && element.TryGetInt32(out var value) ? Typed<int, T>(value) : throw Mismatch(location);
        }

        if (typeof(T) == typeof(long) || typeof(T) == typeof(long?))
        {
            return kind == JsonValueKind.Number && element.TryGetInt64(out var value) ? Typed<long, T>(value) : throw Mismatch(location);
        }

        if (typeof(T) == typeof(decimal) || typeof(T) == typeof(decimal?))
        {
            return kind == JsonValueKind.Number && element.TryGetDecimal(out var value) ? Typed<decimal, T>(value) : throw Mismatch(location);
        }

        if (typeof(T) == typeof(double) || typeof(T) == typeof(double?))
        {
            return kind == JsonValueKind.Number && element.TryGetDouble(out var value) && double.IsFinite(value) ? Typed<double, T>(value) : throw Mismatch(location);
        }

        if (typeof(T) == typeof(bool) || typeof(T) == typeof(bool?))
        {
            return kind is JsonValueKind.True or JsonValueKind.False ? Typed<bool, T>(element.GetBoolean()) : throw Mismatch(location);
        }

        if (typeof(T) == typeof(Guid) || typeof(T) == typeof(Guid?))
        {
            return kind == JsonValueKind.String && element.TryGetGuid(out var value) ? Typed<Guid, T>(value) : throw Mismatch(location);
        }

        if (typeof(T) == typeof(DateTime) || typeof(T) == typeof(DateTime?))
        {
            return kind == JsonValueKind.String && ReadDateTime(element) is { } value ? Typed<DateTime, T>(value) : throw Mismatch(location);
        }

        if (typeof(T) == typeof(DateTimeOffset) || typeof(T) == typeof(DateTimeOffset?))
        {
            return kind == JsonValueKind.String && element.TryGetDateTimeOffset(out var value) ? Typed<DateTimeOffset, T>(value) : throw Mismatch(location);
        }

        if (typeof(T) == typeof(DateOnly) || typeof(T) == typeof(DateOnly?))
        {
            return kind == JsonValueKind.String && ReadDateOnly(element.GetString()!) is { } value ? Typed<DateOnly, T>(value) : throw Mismatch(location);
        }

        return (T)ReadScalar(element, typeof(T), location)!;
    }

    /// <summary>Decodes a JSON array into a list of <typeparamref name="TElement"/>, strictly. Safe for trimming and AOT.</summary>
    /// <param name="element">The JSON value: an array, or null.</param>
    /// <param name="location">The JSON pointer reported on failure.</param>
    /// <typeparam name="TElement">A supported scalar, enum or nullable element type.</typeparam>
    /// <returns>The list, or <see langword="null"/> for a JSON null.</returns>
    /// <exception cref="ProtocolException">The value doesn't fit (code <see cref="ErrorCodes.TypeMismatch"/>).</exception>
    public static List<TElement>? ReadList<TElement>(JsonElement element, string location)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Mismatch(location);
        }

        var list = new List<TElement>(element.GetArrayLength());
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            try
            {
                list.Add(ReadScalar<TElement>(item, location));
            }
            catch (ProtocolException)
            {
                // The element's pointer is only built on failure.
                throw Mismatch($"{location}/{index}");
            }

            index++;
        }

        return list;
    }

    [RequiresDynamicCode("Creates a List<T> or array for a runtime element type.")]
    private static object ReadCollection(JsonElement element, Type type, Type elementType, string location)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Mismatch(location);
        }

        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            list.Add(ReadScalar(item, elementType, $"{location}/{index++}"));
        }

        if (!type.IsArray)
        {
            return list;
        }

        var array = Array.CreateInstance(elementType, list.Count);
        list.CopyTo(array, 0);
        return array;
    }

    private static bool TryReadScalar(JsonElement element, Type type, out object? value)
    {
        value = null;
        if (type == typeof(bool))
        {
            if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = element.GetBoolean();
            }
        }
        else if (element.ValueKind == JsonValueKind.Number)
        {
            value = ReadNumber(element, type);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            value = ReadString(element, type);
        }

        return value is not null;
    }

    private static object? ReadNumber(JsonElement element, Type type)
    {
        if (type == typeof(int)) { return element.TryGetInt32(out var v) ? v : null; }
        if (type == typeof(long)) { return element.TryGetInt64(out var v) ? v : null; }
        if (type == typeof(short)) { return element.TryGetInt16(out var v) ? v : null; }
        if (type == typeof(byte)) { return element.TryGetByte(out var v) ? v : null; }
        if (type == typeof(decimal)) { return element.TryGetDecimal(out var v) ? v : null; }
        if (type == typeof(double)) { return element.TryGetDouble(out var v) && double.IsFinite(v) ? v : null; }
        if (type == typeof(float)) { return element.TryGetSingle(out var v) && float.IsFinite(v) ? v : null; }
        return null;
    }

    private static object? ReadString(JsonElement element, Type type)
    {
        if (type == typeof(string)) { return element.GetString(); }
        if (type == typeof(Guid)) { return element.TryGetGuid(out var v) ? v : null; }
        if (type == typeof(DateTime)) { return ReadDateTime(element); }
        if (type == typeof(DateTimeOffset)) { return element.TryGetDateTimeOffset(out var v) ? v : null; }
        if (type == typeof(byte[])) { return element.TryGetBytesFromBase64(out var v) ? v : null; }

        var text = element.GetString()!;
        if (type == typeof(DateOnly))
        {
            return ReadDateOnly(text);
        }

        if (type == typeof(TimeOnly))
        {
            return TimeOnly.TryParseExact(text, ["HH:mm:ss.FFFFFFF", "HH:mm:ss", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;
        }

        if (type == typeof(TimeSpan))
        {
            return TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        return type.IsEnum ? ReadEnum(text, type) : null;
    }

    /// <summary>
    /// An offset (<c>Z</c>, <c>+02:00</c>) means an instant, read as UTC; no offset means a plain local timestamp
    /// (<see cref="DateTimeKind.Unspecified"/>). Never the server's local time zone.
    /// </summary>
    // Reinterprets a value as T, which is TValue or TValue? (checked by the caller), without boxing.
    private static T Typed<TValue, T>(TValue value)
        where TValue : struct
    {
        if (typeof(T) == typeof(TValue))
        {
            return Unsafe.As<TValue, T>(ref value);
        }

        TValue? nullable = value;
        return Unsafe.As<TValue?, T>(ref nullable);
    }

    private static DateOnly? ReadDateOnly(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    private static DateTime? ReadDateTime(JsonElement element)
    {
        if (!element.TryGetDateTime(out var value))
        {
            return null;
        }

        // System.Text.Json converts numeric offsets to the server's local time; use the instant instead.
        return value.Kind == DateTimeKind.Local && element.TryGetDateTimeOffset(out var instant) ? instant.UtcDateTime : value;
    }

    private static object? ReadEnum(string text, Type type)
    {
        // One name (the usual case): IsDefined matches names exactly, never numbers, without allocating.
        if (!text.Contains(',', StringComparison.Ordinal))
        {
            return Enum.IsDefined(type, text) ? Enum.Parse(type, text) : null;
        }

        var names = Enum.GetNames(type);
        var parts = text.Split(", ");
        if (parts.Length > 1 && !type.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            return null;
        }

        return Array.TrueForAll(parts, part => Array.IndexOf(names, part) >= 0) ? Enum.Parse(type, text) : null;
    }

    private static ProtocolException Mismatch(string location) => new(ErrorCodes.TypeMismatch, location);
}

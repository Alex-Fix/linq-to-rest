using System.Buffers;
using System.Text;
using System.Text.Json;

namespace LinqToRest.Modeling;

/// <summary>Reads and writes the model document, strictly: unknown keys, types and versions are rejected.</summary>
internal static class RestModelJson
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    internal const int MaxSets = 1000;
    internal const int MaxMembers = 1000;
    internal const int MaxEnums = 1000;
    private const int LineWidth = 120;

    public static RestModel Read(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxBytes)
        {
            throw new FormatException($"The model document is larger than {MaxBytes} bytes.");
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = 8 });
            using var document = JsonDocument.ParseValue(ref reader);
            return ReadModel(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The model document isn't valid JSON.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException(exception.Message, exception);
        }
    }

    public static byte[] Write(RestModel model, bool indented)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", RestModel.Version);
            if (model.Enums.Count > 0)
            {
                writer.WriteStartObject("enums");
                foreach (var enumModel in model.Enums)
                {
                    WriteStrings(writer, enumModel.Name, enumModel.Members);
                }

                writer.WriteEndObject();
            }

            writer.WriteStartArray("sets");
            foreach (var set in model.Sets)
            {
                WriteSet(writer, set);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        if (!indented)
        {
            return buffer.WrittenSpan.ToArray();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        var output = new StringBuilder();
        Format(document.RootElement, output, 0, 0);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// <summary>Indents by two spaces, keeping any object or array that fits in <see cref="LineWidth"/> on one line.</summary>
    /// <param name="element">The value to write.</param>
    /// <param name="output">The output.</param>
    /// <param name="indent">The indentation of the line the value starts on.</param>
    /// <param name="column">The column the value starts at.</param>
    private static void Format(JsonElement element, StringBuilder output, int indent, int column)
    {
        var compact = Compact(element);
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array) || column + compact.Length <= LineWidth)
        {
            output.Append(compact);
            return;
        }

        var (open, close) = element.ValueKind == JsonValueKind.Object ? ('{', '}') : ('[', ']');
        output.Append(open).Append('\n');
        var first = true;
        foreach (var (name, value) in Children(element))
        {
            if (!first)
            {
                output.Append(",\n");
            }

            first = false;
            output.Append(' ', indent + 2);
            if (name is not null)
            {
                output.Append(name).Append(": ");
            }

            Format(value, output, indent + 2, indent + 2 + (name?.Length + 2 ?? 0));
        }

        output.Append('\n').Append(' ', indent).Append(close);
    }

    private static string Compact(JsonElement element)
    {
        if (element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return element.GetRawText();
        }

        var parts = Children(element).Select(child => (child.Name is null ? "" : child.Name + ": ") + Compact(child.Value));
        return element.ValueKind == JsonValueKind.Object ? "{" + string.Join(", ", parts) + "}" : "[" + string.Join(", ", parts) + "]";
    }

    private static IEnumerable<(string? Name, JsonElement Value)> Children(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Select(property => ((string?)JsonSerializer.Serialize(property.Name), property.Value))
            : element.EnumerateArray().Select(item => ((string?)null, item));

    private static RestModel ReadModel(JsonElement root)
    {
        var properties = Object(root, "", "v", "enums", "sets");
        if (properties.GetValueOrDefault("v") is not { ValueKind: JsonValueKind.Number } version || !version.TryGetInt32(out var number))
        {
            throw Invalid("/v", "must be a number");
        }

        if (number != RestModel.Version)
        {
            throw new FormatException($"The model document version {number} isn't supported. Expected {RestModel.Version}.");
        }

        var enums = new List<RestEnumModel>();
        if (properties.TryGetValue("enums", out var enumsElement))
        {
            if (enumsElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("/enums", "must be an object");
            }

            foreach (var property in enumsElement.EnumerateObject())
            {
                if (enums.Count == MaxEnums)
                {
                    throw Invalid("/enums", $"has more than {MaxEnums} enums");
                }

                enums.Add(new RestEnumModel { Name = property.Name, Members = Strings(property.Value, $"/enums/{property.Name}", MaxMembers) });
            }
        }

        var sets = Array(Required(properties, "", "sets"), "/sets", MaxSets)
            .Select((element, index) => ReadSet(element, $"/sets/{index}"))
            .ToList();
        return new RestModel(sets, enums);
    }

    private static RestSetModel ReadSet(JsonElement element, string pointer)
    {
        var properties = Object(element, pointer, "name", "type", "key", "concurrencyToken", "writes", "pagination", "properties", "navigations");
        return new RestSetModel
        {
            Name = String(Required(properties, pointer, "name"), pointer + "/name"),
            TypeName = String(Required(properties, pointer, "type"), pointer + "/type"),
            Key = Strings(Required(properties, pointer, "key"), pointer + "/key", MaxMembers),
            ConcurrencyToken = properties.TryGetValue("concurrencyToken", out var token) ? String(token, pointer + "/concurrencyToken") : null,
            Writes = properties.TryGetValue("writes", out var writes) ? ReadWrites(writes, pointer + "/writes") : WriteOperations.None,
            Pagination = ReadPagination(Required(properties, pointer, "pagination"), pointer + "/pagination"),
            Properties = [.. Array(Required(properties, pointer, "properties"), pointer + "/properties", MaxMembers)
                .Select((property, index) => ReadProperty(property, $"{pointer}/properties/{index}"))],
            Navigations = properties.TryGetValue("navigations", out var navigations)
                ? [.. Array(navigations, pointer + "/navigations", MaxMembers).Select((navigation, index) => ReadNavigation(navigation, $"{pointer}/navigations/{index}"))]
                : [],
        };
    }

    private static RestPropertyModel ReadProperty(JsonElement element, string pointer)
    {
        var properties = Object(element, pointer, "name", "type", "nullable", "collection", "writable", "sortable", "filterable");
        return new RestPropertyModel
        {
            Name = String(Required(properties, pointer, "name"), pointer + "/name"),
            Type = String(Required(properties, pointer, "type"), pointer + "/type"),
            IsNullable = Flag(properties, pointer, "nullable", false),
            IsCollection = Flag(properties, pointer, "collection", false),
            IsWritable = Flag(properties, pointer, "writable", false),
            IsSortable = Flag(properties, pointer, "sortable", false),
            IsFilterable = Flag(properties, pointer, "filterable", true),
        };
    }

    private static RestNavigationModel ReadNavigation(JsonElement element, string pointer)
    {
        var properties = Object(element, pointer, "name", "target", "collection", "nullable", "foreignKey");
        return new RestNavigationModel
        {
            Name = String(Required(properties, pointer, "name"), pointer + "/name"),
            Target = String(Required(properties, pointer, "target"), pointer + "/target"),
            IsCollection = Flag(properties, pointer, "collection", false),
            IsNullable = Flag(properties, pointer, "nullable", false),
            ForeignKey = properties.TryGetValue("foreignKey", out var foreignKey) ? Strings(foreignKey, pointer + "/foreignKey", MaxMembers) : [],
        };
    }

    private static RestPaginationModel ReadPagination(JsonElement element, string pointer)
    {
        var properties = Object(element, pointer, "modes", "defaultPageSize", "maxPageSize", "maxSkip");
        var modes = PaginationModes.None;
        foreach (var mode in Strings(Required(properties, pointer, "modes"), pointer + "/modes", 2))
        {
            modes |= mode switch
            {
                "offset" => PaginationModes.Offset,
                "cursor" => PaginationModes.Cursor,
                _ => throw Invalid(pointer + "/modes", $"has the unknown mode '{mode}'"),
            };
        }

        return new RestPaginationModel
        {
            Modes = modes,
            DefaultPageSize = Int32(Required(properties, pointer, "defaultPageSize"), pointer + "/defaultPageSize"),
            MaxPageSize = Int32(Required(properties, pointer, "maxPageSize"), pointer + "/maxPageSize"),
            MaxSkip = Int32(Required(properties, pointer, "maxSkip"), pointer + "/maxSkip"),
        };
    }

    private static WriteOperations ReadWrites(JsonElement element, string pointer)
    {
        var writes = WriteOperations.None;
        foreach (var operation in Strings(element, pointer, 3))
        {
            var flag = operation switch
            {
                "insert" => WriteOperations.Insert,
                "update" => WriteOperations.Update,
                "delete" => WriteOperations.Delete,
                _ => throw Invalid(pointer, $"has the unknown operation '{operation}'"),
            };
            if ((writes & flag) != 0)
            {
                throw Invalid(pointer, $"lists '{operation}' twice");
            }

            writes |= flag;
        }

        return writes;
    }

    private static void WriteSet(Utf8JsonWriter writer, RestSetModel set)
    {
        writer.WriteStartObject();
        writer.WriteString("name", set.Name);
        writer.WriteString("type", set.TypeName);
        WriteStrings(writer, "key", set.Key);
        if (set.ConcurrencyToken is { } token)
        {
            writer.WriteString("concurrencyToken", token);
        }

        writer.WriteStartArray("writes");
        foreach (var (flag, name) in (ReadOnlySpan<(WriteOperations, string)>)[(WriteOperations.Insert, "insert"), (WriteOperations.Update, "update"), (WriteOperations.Delete, "delete")])
        {
            if (set.Allows(flag))
            {
                writer.WriteStringValue(name);
            }
        }

        writer.WriteEndArray();

        var pagination = set.Pagination;
        writer.WriteStartObject("pagination");
        writer.WriteStartArray("modes");
        if ((pagination.Modes & PaginationModes.Offset) != 0)
        {
            writer.WriteStringValue("offset");
        }

        if ((pagination.Modes & PaginationModes.Cursor) != 0)
        {
            writer.WriteStringValue("cursor");
        }

        writer.WriteEndArray();
        writer.WriteNumber("defaultPageSize", pagination.DefaultPageSize);
        writer.WriteNumber("maxPageSize", pagination.MaxPageSize);
        writer.WriteNumber("maxSkip", pagination.MaxSkip);
        writer.WriteEndObject();

        writer.WriteStartArray("properties");
        foreach (var property in set.Properties)
        {
            writer.WriteStartObject();
            writer.WriteString("name", property.Name);
            writer.WriteString("type", property.Type);
            WriteFlag(writer, "nullable", property.IsNullable);
            WriteFlag(writer, "collection", property.IsCollection);
            WriteFlag(writer, "writable", property.IsWritable);
            WriteFlag(writer, "sortable", property.IsSortable);
            if (!property.IsFilterable)
            {
                writer.WriteBoolean("filterable", false);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteStartArray("navigations");
        foreach (var navigation in set.Navigations)
        {
            writer.WriteStartObject();
            writer.WriteString("name", navigation.Name);
            writer.WriteString("target", navigation.Target);
            WriteFlag(writer, "collection", navigation.IsCollection);
            WriteFlag(writer, "nullable", navigation.IsNullable);
            if (navigation.ForeignKey.Count > 0)
            {
                WriteStrings(writer, "foreignKey", navigation.ForeignKey);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteFlag(Utf8JsonWriter writer, string name, bool value)
    {
        if (value)
        {
            writer.WriteBoolean(name, true);
        }
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static Dictionary<string, JsonElement> Object(JsonElement element, string pointer, params ReadOnlySpan<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(pointer, "must be an object");
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid(pointer, $"has the unknown key '{property.Name}'");
            }

            if (!properties.TryAdd(property.Name, property.Value))
            {
                throw Invalid(pointer, $"has the key '{property.Name}' twice");
            }
        }

        return properties;
    }

    private static JsonElement Required(Dictionary<string, JsonElement> properties, string pointer, string name) =>
        properties.TryGetValue(name, out var value) ? value : throw Invalid(pointer, $"is missing '{name}'");

    private static JsonElement.ArrayEnumerator Array(JsonElement element, string pointer, int maxLength)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(pointer, "must be an array");
        }

        if (element.GetArrayLength() > maxLength)
        {
            throw Invalid(pointer, $"has more than {maxLength} entries");
        }

        return element.EnumerateArray();
    }

    private static List<string> Strings(JsonElement element, string pointer, int maxLength) =>
        [.. Array(element, pointer, maxLength).Select((item, index) => String(item, $"{pointer}/{index}"))];

    private static string String(JsonElement element, string pointer) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw Invalid(pointer, "must be a string");

    private static int Int32(JsonElement element, string pointer) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : throw Invalid(pointer, "must be a 32-bit integer");

    private static bool Flag(Dictionary<string, JsonElement> properties, string pointer, string name, bool defaultValue) =>
        !properties.TryGetValue(name, out var value) ? defaultValue
        : value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid($"{pointer}/{name}", "must be true or false"),
        };

    private static FormatException Invalid(string pointer, string problem) =>
        new($"The model document is invalid: '{(pointer.Length == 0 ? "/" : pointer)}' {problem}.");
}

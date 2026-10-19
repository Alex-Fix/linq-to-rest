using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinqToRest.Protocol;

/// <summary>Reads and writes query bodies (<c>QUERY /{set}</c>).</summary>
public static partial class QueryJson
{
    /// <summary>The protocol version written into and required in every body.</summary>
    public const int Version = 1;

    /// <summary>Reads a query body, enforcing <paramref name="limits"/> while building the tree.</summary>
    /// <param name="utf8Json">The UTF-8 body.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <returns>The query.</returns>
    /// <exception cref="ProtocolException">The body is invalid or exceeds a limit.</exception>
    public static QueryRequest Read(ReadOnlyMemory<byte> utf8Json, QueryLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (utf8Json.Length > limits.MaxBodyBytes)
        {
            throw new ProtocolException(ErrorCodes.LimitExceeded, "", "bodySize");
        }

        using var document = Parse(utf8Json, limits.MaxDepth);
        return new Reader(limits).ReadRequest(document.RootElement);
    }

    /// <summary>Writes a query body.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The UTF-8 body.</returns>
    public static byte[] Write(QueryRequest query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRequest(writer, query);
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static JsonDocument Parse(ReadOnlyMemory<byte> utf8Json, int maxNodeDepth)
    {
        try
        {
            // Each node nests at most three JSON levels (object, argument array, lambda object); the node depth limit
            // is the precise check, this only bounds the parser.
            return JsonDocument.Parse(utf8Json, new JsonDocumentOptions { MaxDepth = (maxNodeDepth * 3) + 8, AllowDuplicateProperties = false });
        }
        catch (JsonException)
        {
            throw new ProtocolException(ErrorCodes.InvalidJson, "");
        }
    }

    internal static bool IsIdentifier(string value) => IdentifierPattern().IsMatch(value);

    private static void WriteRequest(Utf8JsonWriter writer, QueryRequest query)
    {
        writer.WriteStartObject();
        writer.WriteNumber("v", Version);
        if (query.Filter is not null)
        {
            writer.WritePropertyName("filter");
            WriteNode(writer, query.Filter);
        }

        if (query.OrderBy.Count > 0)
        {
            writer.WriteStartArray("orderBy");
            foreach (var item in query.OrderBy)
            {
                writer.WriteStartObject();
                writer.WriteString("member", item.Member.Path);
                if (item.Descending)
                {
                    writer.WriteBoolean("desc", true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        if (query.Select is not null)
        {
            writer.WritePropertyName("select");
            WriteNode(writer, query.Select);
        }

        if (query.Include.Count > 0)
        {
            writer.WriteStartArray("include");
            foreach (var path in query.Include)
            {
                writer.WriteStringValue(path.Path);
            }

            writer.WriteEndArray();
        }

        if (query.Page is { } page)
        {
            writer.WriteStartObject("page");
            writer.WriteString("mode", page.Mode == PageMode.Cursor ? "cursor" : "offset");
            if (page.Skip is { } skip)
            {
                writer.WriteNumber("skip", skip);
            }

            if (page.After is { } after)
            {
                writer.WriteString("after", after);
            }

            if (page.Take is { } take)
            {
                writer.WriteNumber("take", take);
            }

            writer.WriteEndObject();
        }

        if (query.Count)
        {
            writer.WriteBoolean("count", true);
        }

        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, QueryNode node)
    {
        switch (node)
        {
            case LiteralNode literal:
                literal.Value.WriteTo(writer);
                break;
            case ListNode list:
                writer.WriteStartArray();
                foreach (var item in list.Items)
                {
                    item.Value.WriteTo(writer);
                }

                writer.WriteEndArray();
                break;
            case MemberNode member:
                writer.WriteStartObject();
                writer.WriteString("member", member.Path);
                writer.WriteEndObject();
                break;
            case VarNode variable:
                writer.WriteStartObject();
                writer.WriteString("var", variable.Name);
                writer.WriteEndObject();
                break;
            case CallNode call:
                writer.WriteStartObject();
                writer.WriteStartArray(call.Op);
                foreach (var argument in call.Arguments)
                {
                    WriteNode(writer, argument);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            case LambdaNode lambda:
                writer.WriteStartObject();
                writer.WriteStartObject(lambda.Op);
                writer.WritePropertyName("source");
                WriteNode(writer, lambda.Source);
                if (lambda.Variable is not null)
                {
                    writer.WriteString("var", lambda.Variable);
                }

                if (lambda.Where is not null)
                {
                    writer.WritePropertyName("where");
                    WriteNode(writer, lambda.Where);
                }

                if (lambda.Body is not null)
                {
                    writer.WritePropertyName("body");
                    WriteNode(writer, lambda.Body);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            case NewNode shape:
                writer.WriteStartObject();
                writer.WriteStartObject("new");
                foreach (var (name, value) in shape.Fields)
                {
                    writer.WritePropertyName(name);
                    WriteNode(writer, value);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                break;
            default:
                throw new ArgumentException($"Unknown node type {node.GetType().Name}.", nameof(node));
        }
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    private sealed class Reader(QueryLimits limits)
    {
        private int _nodes;
        private int _lambdaDepth;

        public QueryRequest ReadRequest(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException(ErrorCodes.InvalidQuery, "");
            }

            var version = root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number) ? number : 0;
            if (version != Version)
            {
                throw new ProtocolException(ErrorCodes.UnsupportedVersion, "/v");
            }

            QueryNode? filter = null, select = null;
            IReadOnlyList<OrderByItem> orderBy = [];
            IReadOnlyList<MemberNode> include = [];
            PageRequest? page = null;
            var count = false;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "v": break;
                    case "filter": filter = ReadNode(property.Value, "/filter", 1, selectContext: false); break;
                    case "select": select = ReadNode(property.Value, "/select", 1, selectContext: true); break;
                    case "orderBy": orderBy = ReadOrderBy(property.Value); break;
                    case "include": include = ReadInclude(property.Value); break;
                    case "page": page = ReadPage(property.Value); break;
                    case "count": count = property.Value.ValueKind == JsonValueKind.True || (property.Value.ValueKind == JsonValueKind.False ? false : throw new ProtocolException(ErrorCodes.InvalidQuery, "/count")); break;
                    default: throw new ProtocolException(ErrorCodes.UnknownNode, "");
                }
            }

            return new QueryRequest { Filter = filter, Select = select, OrderBy = orderBy, Include = include, Page = page, Count = count };
        }

        private List<OrderByItem> ReadOrderBy(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new ProtocolException(ErrorCodes.InvalidQuery, "/orderBy");
            }

            var items = new List<OrderByItem>();
            foreach (var item in element.EnumerateArray())
            {
                var location = $"/orderBy/{items.Count}";
                CountNode(location);
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new ProtocolException(ErrorCodes.InvalidQuery, location);
                }

                MemberNode? member = null;
                var descending = false;
                foreach (var property in item.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "member": member = ReadPath(property.Value, $"{location}/member"); break;
                        case "desc" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False: descending = property.Value.GetBoolean(); break;
                        case "desc": throw new ProtocolException(ErrorCodes.InvalidQuery, $"{location}/desc");
                        default: throw new ProtocolException(ErrorCodes.UnknownNode, location);
                    }
                }

                items.Add(new OrderByItem(member ?? throw new ProtocolException(ErrorCodes.InvalidQuery, location), descending));
            }

            return items;
        }

        private List<MemberNode> ReadInclude(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new ProtocolException(ErrorCodes.InvalidQuery, "/include");
            }

            var paths = new List<MemberNode>();
            foreach (var item in element.EnumerateArray())
            {
                var location = $"/include/{paths.Count}";
                CountNode(location);
                paths.Add(ReadPath(item, location));
            }

            return paths;
        }

        private PageRequest ReadPage(JsonElement element)
        {
            const string location = "/page";
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException(ErrorCodes.InvalidQuery, location);
            }

            PageMode? mode = null;
            int? skip = null, take = null;
            string? after = null;
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "mode" when value.ValueKind == JsonValueKind.String && value.ValueEquals("offset"): mode = PageMode.Offset; break;
                    case "mode" when value.ValueKind == JsonValueKind.String && value.ValueEquals("cursor"): mode = PageMode.Cursor; break;
                    case "skip": skip = ReadCount(value, $"{location}/skip"); break;
                    case "take": take = ReadCount(value, $"{location}/take"); break;
                    case "after" when value.ValueKind == JsonValueKind.String:
                        after = value.GetString()!;
                        if (after.Length > limits.MaxCursorLength)
                        {
                            throw new ProtocolException(ErrorCodes.LimitExceeded, $"{location}/after", "cursorLength");
                        }

                        break;
                    case "mode" or "after": throw new ProtocolException(ErrorCodes.InvalidQuery, $"{location}/{property.Name}");
                    default: throw new ProtocolException(ErrorCodes.UnknownNode, location);
                }
            }

            if (mode is null || (mode == PageMode.Offset && after is not null) || (mode == PageMode.Cursor && skip is not null))
            {
                throw new ProtocolException(ErrorCodes.InvalidQuery, location);
            }

            return new PageRequest { Mode = mode.Value, Skip = skip, Take = take, After = after, Location = location };
        }

        private static int ReadCount(JsonElement value, string location) =>
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0
                ? count
                : throw new ProtocolException(ErrorCodes.InvalidQuery, location);

        private QueryNode ReadNode(JsonElement element, string location, int depth, bool selectContext)
        {
            if (depth > limits.MaxDepth)
            {
                throw new ProtocolException(ErrorCodes.LimitExceeded, location, "depth");
            }

            CountNode(location);
            switch (element.ValueKind)
            {
                case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null:
                    return new LiteralNode(element.Clone(), location);
                case JsonValueKind.Object:
                    break;
                default:
                    throw new ProtocolException(ErrorCodes.UnknownNode, location);
            }

            using var properties = element.EnumerateObject();
            if (!properties.MoveNext())
            {
                throw new ProtocolException(ErrorCodes.UnknownNode, location);
            }

            var (op, value) = (properties.Current.Name, properties.Current.Value);
            if (properties.MoveNext())
            {
                throw new ProtocolException(ErrorCodes.UnknownNode, location);
            }

            var child = $"{location}/{op}";
            switch (op)
            {
                case "member":
                    return ReadPath(value, child, location);
                case "var":
                    return value.ValueKind == JsonValueKind.String && IsIdentifier(value.GetString()!)
                        ? new VarNode(value.GetString()!, location)
                        : throw new ProtocolException(ErrorCodes.UnknownMember, child);
                case "new" when selectContext:
                    return ReadNew(value, child, location, depth);
                case QueryOperators.In:
                    return ReadIn(value, child, location, depth);
                case QueryOperators.Map when !selectContext:
                    throw new ProtocolException(ErrorCodes.UnknownNode, location);
                default:
                    if (QueryOperators.IsLambda(op))
                    {
                        return ReadLambda(op, value, child, location, depth);
                    }

                    if (QueryOperators.TryGetCallArity(op, out var min, out var max))
                    {
                        return ReadCall(op, value, child, location, depth, min, max);
                    }

                    throw new ProtocolException(ErrorCodes.UnknownNode, location);
            }
        }

        private CallNode ReadCall(string op, JsonElement value, string child, string location, int depth, int min, int max)
        {
            if (value.ValueKind != JsonValueKind.Array)
            {
                throw new ProtocolException(ErrorCodes.BadArity, child);
            }

            var length = value.GetArrayLength();
            if (length < min || length > max)
            {
                throw new ProtocolException(ErrorCodes.BadArity, child);
            }

            var arguments = new List<QueryNode>(length);
            foreach (var argument in value.EnumerateArray())
            {
                arguments.Add(ReadNode(argument, $"{child}/{arguments.Count}", depth + 1, selectContext: false));
            }

            return new CallNode(op, arguments, location);
        }

        private CallNode ReadIn(JsonElement value, string child, string location, int depth)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
            {
                throw new ProtocolException(ErrorCodes.BadArity, child);
            }

            var operand = ReadNode(value[0], $"{child}/0", depth + 1, selectContext: false);
            var list = value[1];
            var listLocation = $"{child}/1";
            if (list.ValueKind != JsonValueKind.Array)
            {
                throw new ProtocolException(ErrorCodes.TypeMismatch, listLocation);
            }

            if (list.GetArrayLength() > limits.MaxInListLength)
            {
                throw new ProtocolException(ErrorCodes.LimitExceeded, listLocation, "inListLength");
            }

            // Items are elements of one clone of the list rather than a clone each.
            var items = new List<LiteralNode>(list.GetArrayLength());
            foreach (var item in list.Clone().EnumerateArray())
            {
                var itemLocation = $"{listLocation}/{items.Count}";
                items.Add(item.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
                    ? new LiteralNode(item, itemLocation)
                    : throw new ProtocolException(ErrorCodes.TypeMismatch, itemLocation));
            }

            return new CallNode(QueryOperators.In, [operand, new ListNode(items, listLocation)], location);
        }

        private LambdaNode ReadLambda(string op, JsonElement value, string child, string location, int depth)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException(ErrorCodes.BadArity, child);
            }

            if (++_lambdaDepth > limits.MaxLambdaNesting)
            {
                throw new ProtocolException(ErrorCodes.LimitExceeded, location, "lambdaNesting");
            }

            QueryNode? source = null, body = null, where = null;
            string? variable = null;
            foreach (var property in value.EnumerateObject())
            {
                var propertyLocation = $"{child}/{property.Name}";
                switch (property.Name)
                {
                    case "source": source = ReadNode(property.Value, propertyLocation, depth + 1, selectContext: false); break;
                    case "body": body = ReadNode(property.Value, propertyLocation, depth + 1, selectContext: op == QueryOperators.Map); break;
                    case "where": where = ReadNode(property.Value, propertyLocation, depth + 1, selectContext: false); break;
                    case "var" when property.Value.ValueKind == JsonValueKind.String && IsIdentifier(property.Value.GetString()!):
                        variable = property.Value.GetString();
                        break;
                    case "var": throw new ProtocolException(ErrorCodes.UnknownMember, propertyLocation);
                    default: throw new ProtocolException(ErrorCodes.UnknownNode, child);
                }
            }

            _lambdaDepth--;
            var needsBody = op is QueryOperators.All or QueryOperators.Map;
            if (source is null || (variable is null && (body is not null || where is not null)) || (needsBody && body is null))
            {
                throw new ProtocolException(ErrorCodes.BadArity, child);
            }

            return new LambdaNode(op, source, variable, body, where, location);
        }

        private NewNode ReadNew(JsonElement value, string child, string location, int depth)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException(ErrorCodes.UnknownNode, child);
            }

            var fields = new List<KeyValuePair<string, QueryNode>>();
            foreach (var property in value.EnumerateObject())
            {
                if (!IsIdentifier(property.Name))
                {
                    throw new ProtocolException(ErrorCodes.UnknownNode, child);
                }

                fields.Add(new(property.Name, ReadNode(property.Value, $"{child}/{property.Name}", depth + 1, selectContext: true)));
            }

            return new NewNode(fields, location);
        }

        private MemberNode ReadPath(JsonElement value, string valueLocation, string? nodeLocation = null)
        {
            var path = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
            if (path is null || !Array.TrueForAll(path.Split('/'), IsIdentifier))
            {
                throw new ProtocolException(ErrorCodes.UnknownMember, valueLocation);
            }

            // A lambda variable, the navigations and the final member.
            if (path.Count(c => c == '/') > limits.MaxPathDepth + 1)
            {
                throw new ProtocolException(ErrorCodes.LimitExceeded, valueLocation, "pathDepth");
            }

            return new MemberNode(path, nodeLocation ?? valueLocation);
        }

        private void CountNode(string location)
        {
            if (++_nodes > limits.MaxNodes)
            {
                throw new ProtocolException(ErrorCodes.LimitExceeded, location, "nodes");
            }
        }
    }
}

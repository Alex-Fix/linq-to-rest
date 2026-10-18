using System.Text.Json;

namespace RestToLinq.Protocol;

/// <summary>A node of a query tree.</summary>
public abstract class QueryNode
{
    private protected QueryNode(string location) => Location = location;

    /// <summary>Gets the JSON pointer of the node in the request it was read from (empty when built in code).</summary>
    public string Location { get; }
}

/// <summary>A JSON literal (string, number, boolean or null), typed by the operand it's compared with.</summary>
public sealed class LiteralNode : QueryNode
{
    /// <summary>Creates the node.</summary>
    /// <param name="value">The literal value.</param>
    /// <param name="location">The JSON pointer.</param>
    public LiteralNode(JsonElement value, string location = "")
        : base(location) => Value = value;

    /// <summary>Gets the literal value.</summary>
    public JsonElement Value { get; }

    /// <summary>Creates a literal from a scalar CLR value, encoded like every other value on the wire.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static LiteralNode From(object? value) => new(JsonValues.ToElement(value));
}

/// <summary>The literal list of an <c>in</c> operator.</summary>
public sealed class ListNode : QueryNode
{
    /// <summary>Creates the node.</summary>
    /// <param name="items">The literal items.</param>
    /// <param name="location">The JSON pointer.</param>
    public ListNode(IReadOnlyList<LiteralNode> items, string location = "")
        : base(location) => Items = items;

    /// <summary>Gets the literal items.</summary>
    public IReadOnlyList<LiteralNode> Items { get; }
}

/// <summary>A member path such as <c>price</c>, <c>category/name</c> or <c>l/qty</c> (lambda variable first).</summary>
public sealed class MemberNode : QueryNode
{
    /// <summary>Creates the node.</summary>
    /// <param name="path">The <c>/</c>-separated path.</param>
    /// <param name="location">The JSON pointer.</param>
    public MemberNode(string path, string location = "")
        : base(location)
    {
        Path = path;
        Segments = path.Split('/');
    }

    /// <summary>Gets the <c>/</c>-separated path.</summary>
    public string Path { get; }

    /// <summary>Gets the path segments.</summary>
    public IReadOnlyList<string> Segments { get; }
}

/// <summary>A reference to a lambda variable, used when the variable itself is the value (a primitive element).</summary>
public sealed class VarNode : QueryNode
{
    /// <summary>Creates the node.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="location">The JSON pointer.</param>
    public VarNode(string name, string location = "")
        : base(location) => Name = name;

    /// <summary>Gets the variable name.</summary>
    public string Name { get; }
}

/// <summary>An operator applied to arguments, such as <c>{"gt": [a, b]}</c>.</summary>
public sealed class CallNode : QueryNode
{
    /// <summary>Creates the node.</summary>
    /// <param name="op">The operator, one of <see cref="QueryOperators"/>.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="location">The JSON pointer.</param>
    public CallNode(string op, IReadOnlyList<QueryNode> arguments, string location = "")
        : base(location)
    {
        Op = op;
        Arguments = arguments;
    }

    /// <summary>Gets the operator.</summary>
    public string Op { get; }

    /// <summary>Gets the arguments.</summary>
    public IReadOnlyList<QueryNode> Arguments { get; }
}

/// <summary>A collection lambda such as <c>{"any": {"source": …, "var": "t", "body": …}}</c>.</summary>
public sealed class LambdaNode : QueryNode
{
    /// <summary>Creates the node.</summary>
    /// <param name="op">The operator: any, all, count, sum, min, max, avg or map.</param>
    /// <param name="source">The collection.</param>
    /// <param name="variable">The element variable name, required when there's a body or filter.</param>
    /// <param name="body">The predicate, selector or projection.</param>
    /// <param name="where">An element filter applied before the operator.</param>
    /// <param name="location">The JSON pointer.</param>
    public LambdaNode(string op, QueryNode source, string? variable, QueryNode? body, QueryNode? where, string location = "")
        : base(location)
    {
        Op = op;
        Source = source;
        Variable = variable;
        Body = body;
        Where = where;
    }

    /// <summary>Gets the operator.</summary>
    public string Op { get; }

    /// <summary>Gets the collection.</summary>
    public QueryNode Source { get; }

    /// <summary>Gets the element variable name.</summary>
    public string? Variable { get; }

    /// <summary>Gets the predicate, selector or projection.</summary>
    public QueryNode? Body { get; }

    /// <summary>Gets the element filter.</summary>
    public QueryNode? Where { get; }
}

/// <summary>A projected object shape in a select: <c>{"new": {"id": …, "total": …}}</c>.</summary>
public sealed class NewNode : QueryNode
{
    /// <summary>Creates the node.</summary>
    /// <param name="fields">The fields in order.</param>
    /// <param name="location">The JSON pointer.</param>
    public NewNode(IReadOnlyList<KeyValuePair<string, QueryNode>> fields, string location = "")
        : base(location) => Fields = fields;

    /// <summary>Gets the fields in order.</summary>
    public IReadOnlyList<KeyValuePair<string, QueryNode>> Fields { get; }
}

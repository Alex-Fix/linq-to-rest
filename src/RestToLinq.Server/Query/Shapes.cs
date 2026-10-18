using System.Collections;
using System.Linq.Expressions;
using System.Text.Json;
using RestToLinq.Protocol;
using RestToLinq.Server.Internal;

namespace RestToLinq.Server.Query;

/// <summary>
/// How to write one result row. Rows are flat <c>object[]</c> arrays projected by EF Core; shapes know which index
/// holds which field, so no runtime types are generated.
/// </summary>
internal abstract class Shape
{
    public abstract void Write(Utf8JsonWriter writer, object?[] row);
}

internal sealed class ValueShape(int index) : Shape
{
    public override void Write(Utf8JsonWriter writer, object?[] row) => JsonValues.Write(writer, row[index]);
}

// Presence is the index of a boolean saying whether the object exists (optional navigations).
internal sealed class ObjectShape(IReadOnlyList<KeyValuePair<string, Shape>> fields, int? presence) : Shape
{
    public override void Write(Utf8JsonWriter writer, object?[] row)
    {
        if (presence is { } index && row[index] is not true)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var (name, shape) in fields)
        {
            writer.WritePropertyName(name);
            shape.Write(writer, row);
        }

        writer.WriteEndObject();
    }
}

/// <summary>A collection whose elements are rows of their own.</summary>
internal sealed class CollectionShape(int index, Shape element) : Shape
{
    public override void Write(Utf8JsonWriter writer, object?[] row)
    {
        if (row[index] is not IEnumerable items)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in items)
        {
            element.Write(writer, (object?[])item);
        }

        writer.WriteEndArray();
    }
}

/// <summary>The values projected for one row, in index order.</summary>
internal sealed class Row(ParameterExpression parameter)
{
    private readonly List<Expression> _values = [];

    public ParameterExpression Parameter { get; } = parameter;

    public int Add(Expression value)
    {
        _values.Add(value);
        return _values.Count - 1;
    }

    public LambdaExpression ToLambda() =>
        Expression.Lambda(Expression.NewArrayInit(typeof(object), _values.Select(ExpressionHelpers.ToObject)), Parameter);
}

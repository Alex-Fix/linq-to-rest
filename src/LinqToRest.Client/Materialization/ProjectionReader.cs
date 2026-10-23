using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using LinqToRest.Client.Translation;
using LinqToRest.Protocol;

namespace LinqToRest.Client.Materialization;

/// <summary>
/// Reads the results of a <c>Select</c> from JSON, following the same lambda the translator turned into the select node,
/// so keys and shapes match by construction. Constructors, members and collection types come from the developer's
/// expression tree, which keeps them for trimming and Native AOT; nothing is found by name.
/// </summary>
internal sealed class ProjectionReader
{
    private readonly Dictionary<ParameterExpression, ClientSet?> _parameters = [];

    private ProjectionReader()
    {
    }

    /// <summary>Builds the reader of one projection's items.</summary>
    public static Func<JsonElement, string, object?> Create(ClientSet root, LambdaExpression selector)
    {
        var reader = new ProjectionReader { _parameters = { [selector.Parameters[0]] = root } };
        return reader.Build(Evaluator.PartialEval(selector.Body));
    }

    private Func<JsonElement, string, object?> Build(Expression expression)
    {
        switch (expression)
        {
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert when convert.Type != convert.Operand.Type:
                // The server computes the operand; the conversion happens here, as it would have in the lambda.
                var operand = Build(convert.Operand);
                var target = convert.Type;
                return (element, location) => ConvertValue(operand(element, location), target);
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote } same:
                return Build(same.Operand);
            case NewExpression { Arguments.Count: > 0, Constructor: { } constructor } created:
                return New(created, constructor);
            case MemberInitExpression initializer:
                return Initialize(initializer);
            case MethodCallExpression { Method.Name: nameof(Enumerable.ToList) or nameof(Enumerable.ToArray) } call when call.Method.DeclaringType == typeof(Enumerable):
                return Collection(call.Arguments[0], call.Type);
            case MethodCallExpression { Method.Name: nameof(Enumerable.Select) } call when call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2:
                return Collection(call, call.Type);
        }

        if (TryMember(expression, out var property, out var navigation))
        {
            if (navigation is not null)
            {
                return navigation.IsCollection ? (element, location) => Rows(navigation, element, location) : (element, location) => Row(navigation.Target, element, location);
            }

            if (property is { IsCollection: true })
            {
                return (element, location) => property.Read(element, location);
            }
        }

        // A scalar member, a computed value or a lambda variable: the server sent the value.
        var type = expression.Type;
        return (element, location) => JsonValues.ReadScalar(element, type, location);
    }

    private Func<JsonElement, string, object?> New(NewExpression created, ConstructorInfo constructor)
    {
        var names = created.Members?.Select(member => member.Name) ?? constructor.GetParameters().Select(parameter => parameter.Name!);
        var fields = names.Zip(created.Arguments, (name, argument) => (Key: JsonNamingPolicy.CamelCase.ConvertName(name), Read: Build(argument))).ToArray();
        return (element, location) =>
        {
            var arguments = new object?[fields.Length];
            for (var index = 0; index < fields.Length; index++)
            {
                arguments[index] = fields[index].Read(Field(element, fields[index].Key, location), $"{location}/{fields[index].Key}");
            }

            return constructor.Invoke(arguments);
        };
    }

    private Func<JsonElement, string, object?> Initialize(MemberInitExpression initializer)
    {
        if (initializer.NewExpression.Constructor is not { } constructor || initializer.NewExpression.Arguments.Count > 0)
        {
            throw new NotSupportedException("Projections into a class need a parameterless constructor and member assignments.");
        }

        var assignments = initializer.Bindings
            .Select(binding => binding is MemberAssignment assignment
                ? (assignment.Member, Key: JsonNamingPolicy.CamelCase.ConvertName(assignment.Member.Name), Read: Build(assignment.Expression))
                : throw new NotSupportedException("Only plain member assignments are supported in projections."))
            .ToArray();
        return (element, location) =>
        {
            var result = constructor.Invoke(null);
            foreach (var (member, key, read) in assignments)
            {
                var value = read(Field(element, key, location), $"{location}/{key}");
                switch (member)
                {
                    case PropertyInfo property:
                        property.SetValue(result, value);
                        break;
                    case FieldInfo field:
                        field.SetValue(result, value);
                        break;
                }
            }

            return result;
        };
    }

    /// <summary>A projected collection: <c>x.Select(...)</c>, a collection member, optionally with <c>ToList</c>/<c>ToArray</c>.</summary>
    private Func<JsonElement, string, object?> Collection(Expression source, Type resultType)
    {
        if (resultType.IsInterface)
        {
            throw new NotSupportedException($"Collections in projections need ToList() or ToArray() (here: {resultType.Name}).");
        }

        var element = Element(source);
        return (json, location) =>
        {
            if (json.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var items = new List<object?>(json.GetArrayLength());
            var index = 0;
            foreach (var item in json.EnumerateArray())
            {
                items.Add(element(item, $"{location}/{index++}"));
            }

            return CollectionOf(resultType, items);
        };
    }

    /// <summary>The reader of one element of a projected collection.</summary>
    private Func<JsonElement, string, object?> Element(Expression source)
    {
        LambdaExpression? selector = null;
        if (source is MethodCallExpression { Method.Name: nameof(Enumerable.Select), Arguments.Count: 2 } select && select.Method.DeclaringType == typeof(Enumerable))
        {
            selector = (LambdaExpression)StripQuote(select.Arguments[1]);
            source = select.Arguments[0];
        }

        while (source is MethodCallExpression { Method.Name: nameof(Enumerable.Where), Arguments.Count: 2 } where && where.Method.DeclaringType == typeof(Enumerable))
        {
            source = where.Arguments[0];
        }

        TryMember(source, out var property, out var navigation);
        var elementSet = navigation?.Target;
        if (selector is null)
        {
            if (elementSet is not null)
            {
                return (element, location) => Row(elementSet, element, location);
            }

            var elementType = property?.ElementType ?? typeof(object);
            return (element, location) => JsonValues.ReadScalar(element, elementType, location);
        }

        _parameters[selector.Parameters[0]] = elementSet;
        return Build(selector.Body);
    }

    /// <summary>Resolves <c>p.A.B</c> to the bound member it ends with.</summary>
    private bool TryMember(Expression expression, out ClientProperty? property, out ClientNavigation? navigation)
    {
        (property, navigation) = (null, null);
        var members = new Stack<MemberInfo>();
        var current = expression;
        while (current is MemberExpression member)
        {
            members.Push(member.Member);
            current = member.Expression;
        }

        if (members.Count == 0 || current is not ParameterExpression parameter || !_parameters.TryGetValue(parameter, out var set))
        {
            return false;
        }

        foreach (var member in members)
        {
            if (set is null)
            {
                return false;
            }

            (property, navigation) = (set.Properties.FirstOrDefault(candidate => candidate.Name == member.Name), set.Navigations.FirstOrDefault(candidate => candidate.Name == member.Name));
            set = navigation is { IsCollection: false } ? navigation.Target : null;
        }

        return property is not null || navigation is not null;
    }

    private static object? Row(ClientSet set, JsonElement element, string location) =>
        element.ValueKind == JsonValueKind.Null ? null : RowReader.Read(set, element, location);

    private static IList? Rows(ClientNavigation navigation, JsonElement element, string location)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var list = navigation.NewList();
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            list.Add(RowReader.Read(navigation.Target, item, $"{location}/{index++}"));
        }

        return list;
    }

    private static JsonElement Field(JsonElement element, string key, string location) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value
            : throw new JsonException($"The response's value at '{location}' has no '{key}'.");

    private static object? ConvertValue(object? value, Type target)
    {
        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        return value is null || value.GetType() == underlying
            ? value
            : underlying.IsEnum ? Enum.ToObject(underlying, value) : Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Expression StripQuote(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression;

    /// <summary>
    /// The one place a collection is created for a type only known at run time. The type is the result type of the
    /// developer's own <c>ToList()</c>/<c>ToArray()</c> or member, so the expression tree that names it keeps it (and its
    /// constructor, which <c>ToList</c> itself uses) for trimming and Native AOT; the AOT smoke app exercises it.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "The list type comes from the caller's expression tree, which roots it and its constructor.")]
    private static object CollectionOf(Type resultType, List<object?> items)
    {
        if (resultType.IsArray)
        {
            var array = Array.CreateInstanceFromArrayType(resultType, items.Count);
            for (var index = 0; index < items.Count; index++)
            {
                array.SetValue(items[index], index);
            }

            return array;
        }

        var list = (IList)Activator.CreateInstance(resultType)!;
        foreach (var item in items)
        {
            list.Add(item);
        }

        return list;
    }
}

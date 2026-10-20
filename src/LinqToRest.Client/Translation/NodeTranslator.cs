using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Protocol;

namespace LinqToRest.Client.Translation;

/// <summary>Translates lambda bodies into query nodes. Anything outside the closed node list throws locally.</summary>
internal sealed class NodeTranslator(ClientSet root)
{
    private readonly Dictionary<ParameterExpression, (string? Variable, ClientSet? Type)> _parameters = [];
    private int _variables;

    public QueryNode Predicate(LambdaExpression lambda)
    {
        _parameters[lambda.Parameters[0]] = (null, root);
        return Translate(Evaluator.PartialEval(lambda.Body));
    }

    public MemberNode Path(LambdaExpression lambda, bool navigation)
    {
        _parameters[lambda.Parameters[0]] = (null, root);
        var body = StripConvert(lambda.Body);
        if (!TryPath(body, out var path, out var target) || (navigation ? target.Navigation is null : target.Property is not { IsCollection: false }))
        {
            throw NotSupported(lambda.Body, navigation ? "Include needs a navigation, such as d => d.Category." : "Sort by a scalar entity member, such as d => d.Price.");
        }

        return path;
    }

    /// <summary>The select node, or <see langword="null"/> for <c>d =&gt; d</c>.</summary>
    public QueryNode? Select(LambdaExpression selector)
    {
        _parameters[selector.Parameters[0]] = (null, root);
        var body = Evaluator.PartialEval(selector.Body);
        return body == selector.Parameters[0] ? null : SelectNode(body);
    }

    private QueryNode SelectNode(Expression expression)
    {
        expression = StripConvert(expression);
        switch (expression)
        {
            case NewExpression { Arguments.Count: > 0 } created:
                var names = created.Members?.Select(member => member.Name)
                    ?? created.Constructor!.GetParameters().Select(parameter => parameter.Name!);
                return new NewNode([.. names.Zip(created.Arguments, (name, argument) => KeyValuePair.Create(WireName(name), SelectNode(argument)))]);
            case MemberInitExpression initializer:
                return new NewNode([.. initializer.Bindings.Select(binding => binding is MemberAssignment assignment
                    ? KeyValuePair.Create(WireName(assignment.Member.Name), SelectNode(assignment.Expression))
                    : throw NotSupported(expression, "Only plain member assignments are supported in projections."))]);
            case MethodCallExpression { Method.Name: nameof(Enumerable.ToList) or nameof(Enumerable.ToArray) } call when call.Method.DeclaringType == typeof(Enumerable):
                return SelectNode(call.Arguments[0]);
            case MethodCallExpression { Method.Name: nameof(Enumerable.Select) } call when call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 2:
                var selector = Lambda(call.Arguments[1]);
                var (source, variable, where) = Source(call.Arguments[0], selector.Parameters[0]);
                return new LambdaNode(QueryOperators.Map, source, variable, SelectNode(selector.Body), where);
            default:
                return TryPath(expression, out var path, out _) ? path : Translate(expression);
        }
    }

    private QueryNode Translate(Expression expression)
    {
        expression = StripConvert(expression);
        switch (expression)
        {
            case ConstantExpression constant:
                return Literal(constant.Value, expression);
            case ParameterExpression parameter when _parameters.TryGetValue(parameter, out var info) && info is { Variable: not null, Type: null }:
                return new VarNode(info.Variable);
            case MemberExpression member:
                return Member(member);
            case UnaryExpression { NodeType: ExpressionType.Not } unary when unary.Type == typeof(bool) || unary.Type == typeof(bool?):
                return new CallNode(QueryOperators.Not, [Translate(unary.Operand)]);
            case UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } unary:
                return new CallNode(QueryOperators.Neg, [Translate(unary.Operand)]);
            case BinaryExpression binary:
                return Binary(binary);
            case ConditionalExpression conditional:
                return new CallNode(QueryOperators.If, [Translate(conditional.Test), Translate(conditional.IfTrue), Translate(conditional.IfFalse)]);
            case MethodCallExpression call:
                return Method(call);
            default:
                throw NotSupported(expression, "This expression can't be translated.");
        }
    }

    private QueryNode Member(MemberExpression member)
    {
        if (TryPath(member, out var path, out var target))
        {
            return target.Property is { IsCollection: false } ? path : throw NotSupported(member, "Use a scalar member here; collections need Any, Count, Sum…");
        }

        var inner = member.Expression!;
        var declaring = member.Member.DeclaringType!;
        switch (member.Member.Name)
        {
            case "Count" when TryPath(inner, out var collection, out var counted) && (counted.Property?.IsCollection == true || counted.Navigation?.IsCollection == true):
                return new LambdaNode(QueryOperators.Count, collection, null, null, null);
            case nameof(string.Length) when declaring == typeof(string):
                return new CallNode(QueryOperators.Length, [Translate(inner)]);
            case "Value" when Nullable.GetUnderlyingType(declaring) is not null:
                return Translate(inner);
            case "HasValue" when Nullable.GetUnderlyingType(declaring) is not null:
                return new CallNode(QueryOperators.Ne, [Translate(inner), LiteralNode.From(null)]);
            case "Year" or "Month" or "Day" or "Hour" or "Minute" or "Date"
                when declaring == typeof(DateTime) || declaring == typeof(DateTimeOffset) || declaring == typeof(DateOnly) || declaring == typeof(TimeOnly):
                return new CallNode(char.ToLowerInvariant(member.Member.Name[0]) + member.Member.Name[1..], [Translate(inner)]);
            default:
                throw NotSupported(member, $"{declaring.Name}.{member.Member.Name} isn't part of the entity model or a supported function.");
        }
    }

    private CallNode Binary(BinaryExpression binary)
    {
        switch (binary.NodeType)
        {
            case ExpressionType.AndAlso or ExpressionType.OrElse:
                var op = binary.NodeType == ExpressionType.AndAlso ? QueryOperators.And : QueryOperators.Or;
                return new CallNode(op, [.. Flatten(binary, binary.NodeType).Select(Translate)]);
            case ExpressionType.Add when binary.Type == typeof(string):
                return new CallNode(QueryOperators.Concat, [.. Flatten(binary, ExpressionType.Add).Select(ConcatArgument)]);
            case ExpressionType.Coalesce:
                return new CallNode(QueryOperators.Coalesce, [Translate(binary.Left), Translate(binary.Right)]);
        }

        var comparison = binary.NodeType switch
        {
            ExpressionType.Equal => QueryOperators.Eq,
            ExpressionType.NotEqual => QueryOperators.Ne,
            ExpressionType.GreaterThan => QueryOperators.Gt,
            ExpressionType.GreaterThanOrEqual => QueryOperators.Ge,
            ExpressionType.LessThan => QueryOperators.Lt,
            ExpressionType.LessThanOrEqual => QueryOperators.Le,
            _ => null,
        };

        if (comparison is not null)
        {
            // string.Compare(a, b) > 0, string.CompareOrdinal(a, b) > 0 and a.CompareTo(b) > 0 mean a > b (in the database's collation).
            if (StripConvert(binary.Left) is MethodCallExpression { Method.Name: nameof(string.Compare) or nameof(string.CompareOrdinal) or nameof(string.CompareTo) } compare
                && compare.Method.DeclaringType == typeof(string) && StripConvert(binary.Right) is ConstantExpression { Value: 0 })
            {
                var (left, right) = compare.Object is null ? (compare.Arguments[0], compare.Arguments[1]) : (compare.Object, compare.Arguments[0]);
                return new CallNode(comparison, [Translate(left), Translate(right)]);
            }

            var (a, b) = FixEnums(binary.Left, binary.Right);
            return new CallNode(comparison, [Translate(a), Translate(b)]);
        }

        var arithmetic = binary.NodeType switch
        {
            ExpressionType.Add or ExpressionType.AddChecked => QueryOperators.Add,
            ExpressionType.Subtract or ExpressionType.SubtractChecked => QueryOperators.Sub,
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => QueryOperators.Mul,
            ExpressionType.Divide => QueryOperators.Div,
            ExpressionType.Modulo => QueryOperators.Mod,
            _ => throw NotSupported(binary, $"The operator {binary.NodeType} isn't supported."),
        };
        return new CallNode(arithmetic, [Translate(binary.Left), Translate(binary.Right)]);
    }

    private QueryNode Method(MethodCallExpression call)
    {
        var method = call.Method;
        if (method.DeclaringType == typeof(string))
        {
            switch (method.Name)
            {
                case nameof(string.Contains) or nameof(string.StartsWith) or nameof(string.EndsWith) when call.Object is not null && call.Arguments.Count == 1:
                    return new CallNode(char.ToLowerInvariant(method.Name[0]) + method.Name[1..], [Translate(call.Object), Translate(call.Arguments[0])]);
                case nameof(string.ToLower) or nameof(string.ToLowerInvariant) when call.Arguments.Count == 0:
                    return new CallNode(QueryOperators.ToLower, [Translate(call.Object!)]);
                case nameof(string.ToUpper) or nameof(string.ToUpperInvariant) when call.Arguments.Count == 0:
                    return new CallNode(QueryOperators.ToUpper, [Translate(call.Object!)]);
                case nameof(string.Trim) when call.Arguments.Count == 0:
                    return new CallNode(QueryOperators.Trim, [Translate(call.Object!)]);
                case nameof(string.Equals):
                    return StringEquals(call);
                case nameof(string.Concat) when call.Object is null:
                    var parts = call.Arguments is [NewArrayExpression array] ? array.Expressions : call.Arguments;
                    return new CallNode(QueryOperators.Concat, [.. parts.Select(ConcatArgument)]);
            }
        }

        if (method.DeclaringType == typeof(Enumerable))
        {
            return EnumerableCall(call);
        }

        // list.Contains(d.X) on a captured collection, or the span overload C# 14 binds arrays to. For types that aren't
        // IEquatable<T> (enums) it picks the overload with a comparer, passed as null.
        var arguments = call.Arguments.Count == 3 && StripConvert(call.Arguments[2]) is ConstantExpression { Value: null }
            ? call.Arguments.Take(2).ToList()
            : [.. call.Arguments];
        if (method.Name == nameof(Enumerable.Contains) && arguments.Count is 1 or 2)
        {
            var list = call.Object ?? arguments[0];
            if (list is MethodCallExpression { Method.Name: "op_Implicit", Arguments: [var inner] })
            {
                list = inner;
            }

            if (StripConvert(list) is ConstantExpression { Value: IEnumerable items })
            {
                return In(arguments[^1], items);
            }

            // d.Tags.Contains(x) on a primitive collection of the entity.
            if (call.Object is not null && arguments.Count == 1)
            {
                var (contains, element, where) = Source(call.Object, null);
                return new LambdaNode(QueryOperators.Any, contains, element, new CallNode(QueryOperators.Eq, [new VarNode(element!), Translate(arguments[0])]), where);
            }
        }

        throw NotSupported(call, $"{method.DeclaringType?.Name}.{method.Name} can't be translated.");
    }

    private QueryNode EnumerableCall(MethodCallExpression call)
    {
        var name = call.Method.Name;
        if (name == nameof(Enumerable.Contains))
        {
            if (StripConvert(call.Arguments[0]) is ConstantExpression { Value: IEnumerable items })
            {
                return In(call.Arguments[1], items);
            }

            var (contains, element, where) = Source(call.Arguments[0], null);
            return new LambdaNode(QueryOperators.Any, contains, element, new CallNode(QueryOperators.Eq, [new VarNode(element!), Translate(call.Arguments[1])]), where);
        }

        var op = name switch
        {
            nameof(Enumerable.Any) => QueryOperators.Any,
            nameof(Enumerable.All) => QueryOperators.All,
            nameof(Enumerable.Count) or nameof(Enumerable.LongCount) => QueryOperators.Count,
            nameof(Enumerable.Sum) => QueryOperators.Sum,
            nameof(Enumerable.Min) => QueryOperators.Min,
            nameof(Enumerable.Max) => QueryOperators.Max,
            nameof(Enumerable.Average) => QueryOperators.Avg,
            _ => throw NotSupported(call, $"Enumerable.{name} can't be translated here."),
        };

        var lambda = call.Arguments.Count == 2 ? Lambda(call.Arguments[1]) : null;
        var (source, variable, filter) = Source(call.Arguments[0], lambda?.Parameters[0]);
        var body = lambda is null ? null : Translate(lambda.Body);

        // Count and Any over a filtered source fold the filter into the predicate.
        if (op is QueryOperators.Any or QueryOperators.Count && body is null && filter is not null)
        {
            (body, filter) = (filter, null);
        }

        return new LambdaNode(op, source, body is null && filter is null ? null : variable, body, filter);
    }

    /// <summary>Peels <c>Where</c> calls off a collection source and binds every lambda parameter to one variable.</summary>
    private (MemberNode Source, string? Variable, QueryNode? Where) Source(Expression source, ParameterExpression? parameter)
    {
        var filters = new List<LambdaExpression>();
        source = StripConvert(source);
        while (source is MethodCallExpression { Method.Name: nameof(Enumerable.Where), Arguments.Count: 2 } where && where.Method.DeclaringType == typeof(Enumerable))
        {
            filters.Insert(0, Lambda(where.Arguments[1]));
            source = StripConvert(where.Arguments[0]);
        }

        if (!TryPath(source, out var path, out var target) || target is { Property.IsCollection: false } or { Navigation.IsCollection: false })
        {
            throw NotSupported(source, "Lambdas need a collection member of the entity, such as d => d.Lines.");
        }

        var elementType = target.Navigation?.Target;
        var variable = NextVariable();
        foreach (var bound in filters.Select(filter => filter.Parameters[0]).Append(parameter).OfType<ParameterExpression>())
        {
            _parameters[bound] = (variable, elementType);
        }

        var predicates = filters.Select(filter => Translate(filter.Body)).ToList();
        QueryNode? combined = predicates.Count switch
        {
            0 => null,
            1 => predicates[0],
            _ => new CallNode(QueryOperators.And, predicates),
        };
        return (path, variable, combined);
    }

    /// <summary><c>a.Equals(b)</c> or <c>string.Equals(a, b[, comparison])</c>; ignore-case comparisons compare lowercased.</summary>
    private CallNode StringEquals(MethodCallExpression call)
    {
        var arguments = call.Object is null ? call.Arguments : [call.Object, .. call.Arguments];
        var ignoreCase = arguments.Count == 3 && StripConvert(arguments[2]) is ConstantExpression
        {
            Value: StringComparison.OrdinalIgnoreCase or StringComparison.InvariantCultureIgnoreCase or StringComparison.CurrentCultureIgnoreCase,
        };
        QueryNode Side(Expression side) => ignoreCase ? new CallNode(QueryOperators.ToLower, [Translate(side)]) : Translate(side);
        return arguments.Count is 2 or 3
            ? new CallNode(QueryOperators.Eq, [Side(arguments[0]), Side(arguments[1])])
            : throw NotSupported(call, "This string.Equals overload can't be translated.");
    }

    private CallNode In(Expression item, IEnumerable values)
    {
        // The items are elements of one encoded array rather than a document each.
        var list = JsonValues.ToElement(values.Cast<object?>().Select(value => Sendable(value, item)).ToList());
        return new(QueryOperators.In, [Translate(item), new ListNode([.. list.EnumerateArray().Select(element => new LiteralNode(element))])]);
    }

    private QueryNode ConcatArgument(Expression argument)
    {
        argument = StripConvert(argument);
        return argument is MethodCallExpression { Method.Name: nameof(ToString), Arguments.Count: 0, Object: { } target } ? Translate(target) : Translate(argument);
    }

    private bool TryPath(Expression expression, out MemberNode path, out (ClientProperty? Property, ClientNavigation? Navigation) target)
    {
        var members = new Stack<MemberInfo>();
        var current = expression;
        while (current is MemberExpression member)
        {
            members.Push(member.Member);
            current = member.Expression;
        }

        (path, target) = (null!, default);
        if (current is not ParameterExpression parameter || !_parameters.TryGetValue(parameter, out var info) || members.Count == 0 && info.Type is not null)
        {
            return false;
        }

        var segments = new List<string>();
        if (info.Variable is not null)
        {
            segments.Add(info.Variable);
        }

        var type = info.Type;
        foreach (var member in members)
        {
            if (type is null)
            {
                return false;
            }

            if (type.Properties.FirstOrDefault(candidate => candidate.Name == member.Name) is { } property)
            {
                segments.Add(property.WireName);
                target = (property, null);
                type = null;
            }
            else if (type.Navigations.FirstOrDefault(candidate => candidate.Name == member.Name) is { } navigation)
            {
                segments.Add(navigation.WireName);
                target = (null, navigation);
                type = navigation.IsCollection ? null : navigation.Target;
            }
            else
            {
                throw NotSupported(expression, $"{type.ClrType.Name}.{member.Name} isn't a member of the model.");
            }
        }

        if (members.Count == 0)
        {
            return false;
        }

        path = new MemberNode(string.Join('/', segments));
        return true;
    }

    private string NextVariable()
    {
        string name;
        do
        {
            name = $"v{_variables++}";
        }
        while (root.FindProperty(name) is not null || root.FindNavigation(name) is not null);

        return name;
    }

    private static (Expression Left, Expression Right) FixEnums(Expression left, Expression right)
    {
        // The compiler compares enums as their underlying integers: Convert(d.Status) == 1.
        static Type? EnumOf(Expression e) => StripConvert(e).Type is var type && (Nullable.GetUnderlyingType(type) ?? type) is { IsEnum: true } enumType ? enumType : null;
        if (EnumOf(left) is { } leftEnum && StripConvert(right) is ConstantExpression rightConstant)
        {
            return (StripConvert(left), Expression.Constant(rightConstant.Value is null ? null : Enum.ToObject(leftEnum, rightConstant.Value)));
        }

        if (EnumOf(right) is { } rightEnum && StripConvert(left) is ConstantExpression leftConstant)
        {
            return (Expression.Constant(leftConstant.Value is null ? null : Enum.ToObject(rightEnum, leftConstant.Value)), StripConvert(right));
        }

        return (left, right);
    }

    private static IEnumerable<Expression> Flatten(BinaryExpression binary, ExpressionType type) =>
        new[] { binary.Left, binary.Right }.SelectMany(side => side is BinaryExpression nested && nested.NodeType == type && nested.Type == binary.Type
            ? Flatten(nested, type)
            : [side]);

    private static LiteralNode Literal(object? value, Expression expression) => LiteralNode.From(Sendable(value, expression));

    /// <summary>The value as it's encoded on the wire (a char as a string); throws for values that can't be sent.</summary>
    private static object? Sendable(object? value, Expression expression) => value switch
    {
        null => null,
        char character => character.ToString(),
        _ when ScalarTypes.IsScalar(value.GetType()) => value,
        _ => throw NotSupported(expression, $"Values of type {value.GetType().Name} can't be sent."),
    };

    private static LambdaExpression Lambda(Expression expression) =>
        StripConvert(expression) as LambdaExpression ?? throw NotSupported(expression, "A lambda was expected.");

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.Quote or ExpressionType.TypeAs } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    private static string WireName(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    internal static NotSupportedException NotSupported(Expression expression, string reason) =>
        new($"LinqToRest can't translate '{expression}'. {reason}");
}

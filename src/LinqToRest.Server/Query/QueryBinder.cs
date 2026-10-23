using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Protocol;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;

namespace LinqToRest.Server.Query;

/// <summary>
/// Type-checks query nodes against the set model and builds expressions over entities from them. Only the closed node
/// list is accepted; every member is resolved through the set configuration, never by reflection on client strings.
/// </summary>
internal sealed partial class QueryBinder(ServerModel server, RequestPolicies policies)
{
    private static readonly HashSet<Type> OrderedTypes =
    [
        typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double), typeof(decimal),
        typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan),
    ];

    private static readonly MethodInfo StringCompare = typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;
    private static readonly MethodInfo StringConcat = typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!;

    private int _parameters;

    public Expression Predicate(QueryNode node, Scope scope) => Bool(node, scope, MemberUse.Filter);

    public Bound Member(MemberNode node, Scope scope, MemberUse use)
    {
        var segments = node.Segments;
        if (scope.Fields is { } fields)
        {
            // Having and order of a grouped query: paths name the select's fields.
            return segments.Count == 1 && fields.TryGetValue(segments[0], out var field) ? field : throw UnknownMember(node);
        }

        Bound current = scope.Root;
        var index = 0;
        if (scope.Variables.TryGetValue(segments[0], out var variable))
        {
            current = variable;
            index = 1;
        }
        else if (scope.RootHidden)
        {
            throw RequestException.Invalid(ErrorCodes.InvalidQuery, node.Location);
        }

        var navigations = 0;
        for (; index < segments.Count; index++)
        {
            var last = index == segments.Count - 1;
            if (current is not EntityBound entity)
            {
                throw UnknownMember(node);
            }

            var set = entity.Mapping;
            if (set.FindProperty(segments[index]) is { } property)
            {
                if (!last)
                {
                    throw UnknownMember(node);
                }

                CheckUse(property, use, node);
                if (property.IsCollection)
                {
                    var source = GuardSequence(entity, ExpressionHelpers.StripMaterialization(ExpressionHelpers.Inline(property.Binding, entity.Expression)), property.ElementType!);
                    return new CollectionBound(source, property.ElementType!, null, PropertyValue(entity, property));
                }

                return new ValueBound(PropertyValue(entity, property), property.IsNullable || entity.NeedsLifting);
            }

            if (set.FindNavigation(segments[index]) is not { } navigation)
            {
                throw UnknownMember(node);
            }

            if (++navigations > server.Limits.MaxPathDepth)
            {
                throw RequestException.LimitExceeded(node.Location, "pathDepth");
            }

            if (navigation.IsCollection)
            {
                if (!last)
                {
                    throw UnknownMember(node);
                }

                return new CollectionBound(CollectionSource(entity, navigation), navigation.Target.EntityType, navigation.Target, null);
            }

            current = Reference(entity, navigation);
        }

        return current;
    }

    public Expression Value(QueryNode node, Scope scope, Type? expected, MemberUse use) => node switch
    {
        LiteralNode literal => Literal(literal, expected),
        MemberNode member => Member(member, scope, use) is ValueBound value ? value.Expression : throw TypeMismatch(node),
        VarNode variable => scope.Variables.TryGetValue(variable.Name, out var bound)
            ? bound is ValueBound value ? value.Expression : throw TypeMismatch(node)
            : throw UnknownMember(node),
        CallNode call => Call(call, scope, use),
        LambdaNode { Op: not QueryOperators.Map } lambda => Aggregate(lambda, scope, use),
        KeyNode key => scope.Group is { } group && key.Index < group.Keys.Count
            ? group.Keys[key.Index].Expression
            : throw RequestException.Invalid(ErrorCodes.InvalidQuery, node.Location),
        GroupNode => throw RequestException.Invalid(ErrorCodes.InvalidQuery, node.Location),
        _ => throw TypeMismatch(node),
    };

    public string NextParameterName() => $"x{_parameters++}";

    public EntityBound Reference(EntityBound entity, SetNavigation navigation)
    {
        var target = ExpressionHelpers.Inline(navigation.Entity, entity.Expression);
        var guard = entity.Guard;
        if (policies.Filter(navigation.Target) is { } predicate)
        {
            guard = ExpressionHelpers.AndAlso(guard, Expression.AndAlso(NotNull(target), ExpressionHelpers.Inline(predicate, target)));
        }

        return new EntityBound(target, navigation.Target, guard, entity.IsNullable || navigation.IsNullable);
    }

    public Expression CollectionSource(EntityBound entity, SetNavigation navigation)
    {
        var source = ExpressionHelpers.Inline(navigation.Entity, entity.Expression);
        if (policies.Filter(navigation.Target) is { } predicate)
        {
            source = Where(source, predicate);
        }

        return GuardSequence(entity, source, navigation.Target.EntityType);
    }

    /// <summary>A member's value, lifted to nullable and guarded when it's read through an optional or filtered navigation.</summary>
    public static Expression PropertyValue(EntityBound entity, SetProperty property)
    {
        var value = ExpressionHelpers.Inline(property.Binding, entity.Expression);
        if (!entity.NeedsLifting)
        {
            return value;
        }

        value = ExpressionHelpers.MakeNullable(value);
        return entity.Guard is null ? value : Expression.Condition(entity.Guard, value, Expression.Constant(null, value.Type));
    }

    public static Expression Presence(EntityBound entity) => entity.Guard ?? NotNull(entity.Expression);

    /// <summary>
    /// Projects each element to its row. Set rows are ordered by their key: without an order, databases return
    /// collection elements in any order (PostgreSQL does), so the same query could answer differently.
    /// </summary>
    public static Expression RowsList(Expression source, Row elementRow, SetMapping? elementMapping)
    {
        var selector = elementRow.ToLambda();
        var elementType = elementRow.Parameter.Type;
        if (elementMapping is not null)
        {
            source = OrderByKey(source, elementType, elementMapping);
        }

        var select = Expression.Call(EnumerableMethods.Select.MakeGenericMethod(elementType, typeof(object[])), source, selector);
        return Expression.Call(EnumerableMethods.ToList.MakeGenericMethod(typeof(object[])), select);
    }

    private static Expression OrderByKey(Expression source, Type elementType, SetMapping mapping)
    {
        var element = Expression.Parameter(elementType, "k");
        for (var index = 0; index < mapping.Key.Length; index++)
        {
            var key = Expression.Lambda(ExpressionHelpers.Inline(mapping.Key[index].Binding, element), element);
            var method = index == 0 ? EnumerableMethods.OrderBy : EnumerableMethods.ThenBy;
            source = Expression.Call(method.MakeGenericMethod(elementType, key.ReturnType), source, key);
        }

        return source;
    }

    public static RequestException UnknownMember(QueryNode node) => RequestException.Invalid(ErrorCodes.UnknownMember, node.Location);

    public static RequestException TypeMismatch(QueryNode node) => RequestException.Invalid(ErrorCodes.TypeMismatch, node.Location);

    private static void CheckUse(SetProperty property, MemberUse use, QueryNode node)
    {
        if (use == MemberUse.Filter && !property.IsFilterable)
        {
            throw RequestException.Invalid(ErrorCodes.NotFilterable, node.Location);
        }

        if (use == MemberUse.Sort && !property.IsSortable)
        {
            throw RequestException.Invalid(ErrorCodes.NotSortable, node.Location);
        }

        if (use == MemberUse.Group && !property.IsGroupable)
        {
            throw RequestException.Invalid(ErrorCodes.NotGroupable, node.Location);
        }

        if (use == MemberUse.Aggregate && !property.IsAggregatable)
        {
            throw RequestException.Invalid(ErrorCodes.NotAggregatable, node.Location);
        }
    }

    private static Expression GuardSequence(EntityBound entity, Expression source, Type elementType) =>
        entity.Guard is null ? source : Where(source, Expression.Lambda(entity.Guard, Expression.Parameter(elementType, "_")));

    private static MethodCallExpression Where(Expression source, LambdaExpression predicate) =>
        Expression.Call(EnumerableMethods.Where.MakeGenericMethod(predicate.Parameters[0].Type), source, predicate);

    private static BinaryExpression NotNull(Expression value) => Expression.NotEqual(value, Expression.Constant(null, value.Type));

    private Expression Bool(QueryNode node, Scope scope, MemberUse use)
    {
        var value = Value(node, scope, typeof(bool), use);
        if (value.Type == typeof(bool?))
        {
            return Expression.Equal(value, Expression.Constant(true, typeof(bool?)));
        }

        return value.Type == typeof(bool) ? value : throw TypeMismatch(node);
    }

    private Expression Str(QueryNode node, Scope scope, MemberUse use)
    {
        var value = Value(node, scope, typeof(string), use);
        return value.Type == typeof(string) ? value : throw TypeMismatch(node);
    }

    private static Expression Literal(LiteralNode literal, Type? expected)
    {
        var type = expected ?? NaturalType(literal.Value) ?? throw TypeMismatch(literal);
        return ExpressionHelpers.Parameter(JsonValues.Read(literal.Value, type, literal.Location), type);
    }

    private static Type? NaturalType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => typeof(string),
        JsonValueKind.True or JsonValueKind.False => typeof(bool),
        JsonValueKind.Number when value.TryGetInt32(out _) => typeof(int),
        JsonValueKind.Number when value.TryGetInt64(out _) => typeof(long),
        JsonValueKind.Number when value.TryGetDecimal(out _) => typeof(decimal),
        JsonValueKind.Number => typeof(double),
        _ => null,
    };

    private static Type LiteralTarget(Type other, LiteralNode literal) =>
        literal.Value.ValueKind == JsonValueKind.Null ? ExpressionHelpers.MakeNullable(other) : other;

    /// <summary>Binds two operands, typing a literal from the other side.</summary>
    private (Expression Left, Expression Right) Operands(QueryNode left, QueryNode right, Scope scope, MemberUse use)
    {
        if (left is LiteralNode leftLiteral)
        {
            var other = right is LiteralNode rightLiteral ? Literal(rightLiteral, null) : Value(right, scope, null, use);
            return (Literal(leftLiteral, LiteralTarget(other.Type, leftLiteral)), other);
        }

        var value = Value(left, scope, null, use);
        return (value, right is LiteralNode literal ? Literal(literal, LiteralTarget(value.Type, literal)) : Value(right, scope, null, use));
    }

    private static (Expression Left, Expression Right) Unify(Expression left, Expression right, QueryNode node, bool arithmetic)
    {
        var (leftType, rightType) = (ExpressionHelpers.Underlying(left.Type), ExpressionHelpers.Underlying(right.Type));
        Type target;
        if (ScalarTypes.IsNumeric(leftType) && ScalarTypes.IsNumeric(rightType))
        {
            target = Promote(leftType, rightType, arithmetic) ?? throw TypeMismatch(node);
        }
        else if (leftType == rightType)
        {
            target = leftType;
        }
        else
        {
            throw TypeMismatch(node);
        }

        if (target.IsValueType && (Nullable.GetUnderlyingType(left.Type) is not null || Nullable.GetUnderlyingType(right.Type) is not null))
        {
            target = ExpressionHelpers.MakeNullable(target);
        }

        return (Convert(left, target), Convert(right, target));
    }

    private static Type? Promote(Type left, Type right, bool arithmetic)
    {
        Type[] ranks = [typeof(byte), typeof(short), typeof(int), typeof(long), typeof(float), typeof(double)];
        if (left == typeof(decimal) || right == typeof(decimal))
        {
            var other = left == typeof(decimal) ? right : left;
            return other == typeof(float) || other == typeof(double) ? null : typeof(decimal);
        }

        var rank = Math.Max(Array.IndexOf(ranks, left), Array.IndexOf(ranks, right));
        return ranks[arithmetic ? Math.Max(rank, 2) : rank];
    }

    private static Expression Convert(Expression value, Type type) => value.Type == type ? value : Expression.Convert(value, type);

    private static Expression EnumToUnderlying(Expression value)
    {
        var type = ExpressionHelpers.Underlying(value.Type);
        if (!type.IsEnum)
        {
            return value;
        }

        var underlying = Enum.GetUnderlyingType(type);
        return Expression.Convert(value, value.Type == type ? underlying : ExpressionHelpers.MakeNullable(underlying));
    }

    private Expression Call(CallNode node, Scope scope, MemberUse use)
    {
        var arguments = node.Arguments;
        switch (node.Op)
        {
            case QueryOperators.And or QueryOperators.Or:
                var parts = arguments.Select(argument => Bool(argument, scope, use)).ToList();
                return parts.Skip(1).Aggregate(parts[0], node.Op == QueryOperators.And ? Expression.AndAlso : Expression.OrElse);
            case QueryOperators.Not:
                return Expression.Not(Bool(arguments[0], scope, use));
            case QueryOperators.Eq or QueryOperators.Ne or QueryOperators.Gt or QueryOperators.Ge or QueryOperators.Lt or QueryOperators.Le:
                return Compare(node, scope, use);
            case QueryOperators.In:
                return In(node, scope, use);
            case QueryOperators.Add or QueryOperators.Sub or QueryOperators.Mul or QueryOperators.Div or QueryOperators.Mod:
                return Arithmetic(node, scope, use);
            case QueryOperators.Neg:
                var operand = Value(arguments[0], scope, null, use);
                if (!ScalarTypes.IsNumeric(operand.Type))
                {
                    throw TypeMismatch(node);
                }

                var promoted = Promote(ExpressionHelpers.Underlying(operand.Type), typeof(byte), arithmetic: true)!;
                return Expression.Negate(Convert(operand, Nullable.GetUnderlyingType(operand.Type) is null ? promoted : ExpressionHelpers.MakeNullable(promoted)));
            case QueryOperators.Contains or QueryOperators.StartsWith or QueryOperators.EndsWith:
                var method = typeof(string).GetMethod(char.ToUpperInvariant(node.Op[0]) + node.Op[1..], [typeof(string)])!;
                return Expression.Call(Str(arguments[0], scope, use), method, Str(arguments[1], scope, use));
            case QueryOperators.ToLower:
                return Expression.Call(Str(arguments[0], scope, use), typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!);
            case QueryOperators.ToUpper:
                return Expression.Call(Str(arguments[0], scope, use), typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!);
            case QueryOperators.Trim:
                return Expression.Call(Str(arguments[0], scope, use), typeof(string).GetMethod(nameof(string.Trim), Type.EmptyTypes)!);
            case QueryOperators.Length:
                return Expression.Property(Str(arguments[0], scope, use), nameof(string.Length));
            case QueryOperators.Concat:
                var strings = arguments.Select(argument => AsString(Value(argument, scope, null, use), argument)).ToList();
                return strings.Skip(1).Aggregate(strings[0], (left, right) => Expression.Call(StringConcat, left, right));
            case QueryOperators.Year or QueryOperators.Month or QueryOperators.Day or QueryOperators.Hour or QueryOperators.Minute or QueryOperators.Date:
                return DatePart(node, scope, use);
            case QueryOperators.If:
                var condition = Bool(arguments[0], scope, use);
                var (whenTrue, whenFalse) = Operands(arguments[1], arguments[2], scope, use);
                (whenTrue, whenFalse) = Unify(whenTrue, whenFalse, node, arithmetic: false);
                return Expression.Condition(condition, whenTrue, whenFalse);
            case QueryOperators.Coalesce:
                return Coalesce(node, scope, use);
            default:
                throw RequestException.Invalid(ErrorCodes.UnknownNode, node.Location);
        }
    }

    private Expression Compare(CallNode node, Scope scope, MemberUse use)
    {
        var (left, right) = Operands(node.Arguments[0], node.Arguments[1], scope, use);
        (left, right) = Unify(left, right, node, arithmetic: false);
        (left, right) = (EnumToUnderlying(left), EnumToUnderlying(right));
        var kind = node.Op switch
        {
            QueryOperators.Eq => ExpressionType.Equal,
            QueryOperators.Ne => ExpressionType.NotEqual,
            QueryOperators.Gt => ExpressionType.GreaterThan,
            QueryOperators.Ge => ExpressionType.GreaterThanOrEqual,
            QueryOperators.Lt => ExpressionType.LessThan,
            _ => ExpressionType.LessThanOrEqual,
        };

        return Comparison(kind, left, right) ?? throw TypeMismatch(node);
    }

    /// <summary>Builds a comparison EF Core translates, or <see langword="null"/> when the type can't be compared that way.</summary>
    public static Expression? Comparison(ExpressionType kind, Expression left, Expression right)
    {
        var type = ExpressionHelpers.Underlying(left.Type);
        if (kind is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            try
            {
                return Expression.MakeBinary(kind, left, right);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        if (type == typeof(string))
        {
            return Expression.MakeBinary(kind, Expression.Call(StringCompare, left, right), Expression.Constant(0));
        }

        return OrderedTypes.Contains(type) ? Expression.MakeBinary(kind, left, right) : null;
    }

    private Expression In(CallNode node, Scope scope, MemberUse use)
    {
        if (node.Arguments[0] is LiteralNode)
        {
            throw TypeMismatch(node);
        }

        var left = Value(node.Arguments[0], scope, null, use);
        var list = (ListNode)node.Arguments[1];
        var listType = typeof(List<>).MakeGenericType(left.Type);
        var items = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in list.Items)
        {
            items.Add(JsonValues.Read(item.Value, left.Type, item.Location));
        }

        return ExpressionHelpers.Contains(ExpressionHelpers.Parameter(items, listType), left);
    }

    private BinaryExpression Arithmetic(CallNode node, Scope scope, MemberUse use)
    {
        var (left, right) = Operands(node.Arguments[0], node.Arguments[1], scope, use);
        if (!ScalarTypes.IsNumeric(left.Type) || !ScalarTypes.IsNumeric(right.Type))
        {
            throw TypeMismatch(node);
        }

        (left, right) = Unify(left, right, node, arithmetic: true);
        var kind = node.Op switch
        {
            QueryOperators.Add => ExpressionType.Add,
            QueryOperators.Sub => ExpressionType.Subtract,
            QueryOperators.Mul => ExpressionType.Multiply,
            QueryOperators.Div => ExpressionType.Divide,
            _ => ExpressionType.Modulo,
        };

        return Expression.MakeBinary(kind, left, right);
    }

    private static Expression AsString(Expression value, QueryNode node)
    {
        if (value.Type == typeof(string))
        {
            return value;
        }

        return ScalarTypes.IsScalar(value.Type) && value.Type != typeof(byte[])
            ? Expression.Call(value, value.Type.GetMethod(nameof(ToString), Type.EmptyTypes)!)
            : throw TypeMismatch(node);
    }

    private Expression DatePart(CallNode node, Scope scope, MemberUse use)
    {
        var value = Value(node.Arguments[0], scope, null, use);
        var type = ExpressionHelpers.Underlying(value.Type);
        Type[] allowed = node.Op switch
        {
            QueryOperators.Year or QueryOperators.Month or QueryOperators.Day => [typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly)],
            QueryOperators.Hour or QueryOperators.Minute => [typeof(DateTime), typeof(DateTimeOffset), typeof(TimeOnly)],
            _ => [typeof(DateTime), typeof(DateTimeOffset)],
        };

        if (Array.IndexOf(allowed, type) < 0)
        {
            throw TypeMismatch(node);
        }

        var nullable = value.Type != type;
        var source = nullable ? Expression.Property(value, nameof(Nullable<>.Value)) : value;
        var part = Expression.Property(source, char.ToUpperInvariant(node.Op[0]) + node.Op[1..]);
        return nullable ? ExpressionHelpers.MakeNullable(part) : part;
    }

    private BinaryExpression Coalesce(CallNode node, Scope scope, MemberUse use)
    {
        if (node.Arguments[0] is LiteralNode)
        {
            throw TypeMismatch(node);
        }

        var left = Value(node.Arguments[0], scope, null, use);
        if (!ExpressionHelpers.IsNullable(left.Type))
        {
            throw TypeMismatch(node);
        }

        var underlying = ExpressionHelpers.Underlying(left.Type);
        var right = node.Arguments[1] is LiteralNode literal ? Literal(literal, underlying) : Value(node.Arguments[1], scope, null, use);
        return right.Type == underlying || right.Type == left.Type ? Expression.Coalesce(left, right) : throw TypeMismatch(node);
    }
}

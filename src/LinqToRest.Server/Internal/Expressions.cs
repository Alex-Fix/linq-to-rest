using System.Linq.Expressions;

namespace LinqToRest.Server.Internal;

internal static class ExpressionHelpers
{
    /// <summary>Inlines <paramref name="lambda"/> with its parameter replaced by <paramref name="argument"/>.</summary>
    public static Expression Inline(LambdaExpression lambda, Expression argument) =>
        new ReplaceVisitor(lambda.Parameters[0], argument).Visit(lambda.Body);

    /// <summary>Removes a trailing ToList/ToArray, which composes worse inside predicates than the sequence itself.</summary>
    public static Expression StripMaterialization(Expression expression) =>
        expression is MethodCallExpression { Method.Name: nameof(Enumerable.ToList) or nameof(Enumerable.ToArray) } call
        && call.Method.DeclaringType == typeof(Enumerable)
            ? call.Arguments[0]
            : expression;

    /// <summary>A value EF Core turns into a SQL parameter rather than inlining it as a literal.</summary>
    public static Expression Parameter(object? value, Type type)
    {
        var box = Activator.CreateInstance(typeof(Box<>).MakeGenericType(type), value)!;
        return Expression.Property(Expression.Constant(box), nameof(Box<>.Value));
    }

    public static Expression ToObject(Expression expression) =>
        expression.Type == typeof(object) ? expression : Expression.Convert(expression, typeof(object));

    public static Type MakeNullable(Type type) =>
        type.IsValueType && Nullable.GetUnderlyingType(type) is null ? typeof(Nullable<>).MakeGenericType(type) : type;

    public static Expression MakeNullable(Expression expression) =>
        expression.Type == MakeNullable(expression.Type) ? expression : Expression.Convert(expression, MakeNullable(expression.Type));

    public static bool IsNullable(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    public static Type Underlying(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    public static Type? GetSequenceElementType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        return type.GetInterfaces()
            .FirstOrDefault(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
    }

    public static Expression Contains(Expression list, Expression item) =>
        Expression.Call(EnumerableMethods.Contains.MakeGenericMethod(item.Type), list, item);

    public static Expression AndAlso(Expression? left, Expression right) => left is null ? right : Expression.AndAlso(left, right);

    private sealed class ReplaceVisitor(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? replacement : node;
    }
}

internal sealed class Box<T>(T value)
{
    public T Value { get; } = value;
}

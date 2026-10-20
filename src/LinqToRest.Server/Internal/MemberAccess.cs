using System.Linq.Expressions;
using System.Reflection;

namespace LinqToRest.Server.Internal;

internal static class MemberAccess
{
    /// <summary>Returns the property of a <c>e =&gt; e.Property</c> lambda.</summary>
    public static PropertyInfo GetProperty(LambdaExpression lambda, string argumentName) =>
        TryGetProperty(lambda.Body, lambda.Parameters[0], out var property)
            ? property
            : throw new ArgumentException("The expression must be a direct property access, such as 'e => e.Id'.", argumentName);

    /// <summary>Returns the properties of <c>e =&gt; e.Id</c> or <c>e =&gt; new { e.A, e.B }</c>.</summary>
    public static IReadOnlyList<PropertyInfo> GetProperties(LambdaExpression lambda, string argumentName)
    {
        if (StripConvert(lambda.Body) is NewExpression { Arguments.Count: > 0 } created)
        {
            return [.. created.Arguments.Select(argument => GetProperty(Expression.Lambda(argument, lambda.Parameters), argumentName))];
        }

        return [GetProperty(lambda, argumentName)];
    }

    /// <summary>Returns whether <paramref name="body"/> is <c>parameter.Property</c> (ignoring conversions).</summary>
    public static bool TryGetProperty(Expression body, ParameterExpression parameter, out PropertyInfo property)
    {
        if (StripConvert(body) is MemberExpression { Member: PropertyInfo found } member && member.Expression == parameter)
        {
            property = found;
            return true;
        }

        property = null!;
        return false;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }
}

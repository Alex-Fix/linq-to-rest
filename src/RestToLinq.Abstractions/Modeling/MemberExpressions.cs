using System.Linq.Expressions;
using System.Reflection;

namespace RestToLinq.Modeling;

internal static class MemberExpressions
{
    /// <summary>Returns the property of a <c>d =&gt; d.Property</c> lambda.</summary>
    public static PropertyInfo GetProperty(LambdaExpression lambda, string argumentName)
    {
        var body = StripConvert(lambda.Body);
        if (body is MemberExpression { Member: PropertyInfo property } member && member.Expression == lambda.Parameters[0])
        {
            return property;
        }

        throw new ArgumentException("The expression must be a direct property access, such as 'd => d.Id'.", argumentName);
    }

    /// <summary>Returns the properties of <c>d =&gt; d.Id</c> or <c>d =&gt; new { d.A, d.B }</c>.</summary>
    public static IReadOnlyList<PropertyInfo> GetProperties(LambdaExpression lambda, string argumentName)
    {
        if (StripConvert(lambda.Body) is NewExpression { Arguments.Count: > 0 } created)
        {
            return [.. created.Arguments.Select(argument => GetProperty(Expression.Lambda(argument, lambda.Parameters), argumentName))];
        }

        return [GetProperty(lambda, argumentName)];
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

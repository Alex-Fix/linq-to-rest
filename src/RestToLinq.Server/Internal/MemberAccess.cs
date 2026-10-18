using System.Linq.Expressions;
using System.Reflection;

namespace RestToLinq.Server.Internal;

internal static class MemberAccess
{
    /// <summary>Returns the property of a <c>d =&gt; d.Property</c> lambda.</summary>
    public static PropertyInfo GetProperty(LambdaExpression lambda, string argumentName) =>
        TryGetProperty(lambda.Body, lambda.Parameters[0], out var property)
            ? property
            : throw new ArgumentException("The expression must be a direct property access, such as 'd => d.Id'.", argumentName);

    /// <summary>Returns whether <paramref name="body"/> is <c>parameter.Property</c> (ignoring conversions).</summary>
    public static bool TryGetProperty(Expression body, ParameterExpression parameter, out PropertyInfo property)
    {
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            body = unary.Operand;
        }

        if (body is MemberExpression { Member: PropertyInfo found } member && member.Expression == parameter)
        {
            property = found;
            return true;
        }

        property = null!;
        return false;
    }
}

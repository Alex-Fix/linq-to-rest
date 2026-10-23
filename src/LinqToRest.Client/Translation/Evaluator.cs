using System.Linq.Expressions;
using System.Reflection;

namespace LinqToRest.Client.Translation;

/// <summary>
/// Replaces every subtree that doesn't depend on a lambda parameter (captured variables, method calls on them) with
/// its value. This runs the developer's own code on the client, never anything from the server.
/// </summary>
internal static class Evaluator
{
    public static Expression PartialEval(Expression expression)
    {
        var candidates = new Nominator();
        candidates.Visit(expression);
        return new SubtreeEvaluator(candidates.Candidates).Visit(expression)!;
    }

    private sealed class Nominator : ExpressionVisitor
    {
        private bool _blocked;

        public HashSet<Expression> Candidates { get; } = [];

        public override Expression? Visit(Expression? node)
        {
            if (node is null)
            {
                return null;
            }

            var saved = _blocked;
            _blocked = false;
            base.Visit(node);
            if (!_blocked)
            {
                if (CanEvaluate(node))
                {
                    Candidates.Add(node);
                }
                else
                {
                    _blocked = true;
                }
            }

            _blocked |= saved;
            return node;
        }

        // Spans can't be boxed, lambdas and queries aren't values, and parameters are what the server binds.
        private static bool CanEvaluate(Expression node) =>
            node.NodeType is not (ExpressionType.Parameter or ExpressionType.Lambda or ExpressionType.Quote)
            && !node.Type.IsByRefLike
            && !(node is ConstantExpression { Value: IQueryable })
            && !(node is MethodCallExpression { Method.DeclaringType: var type } && type == typeof(Queryable));
    }

    private sealed class SubtreeEvaluator(HashSet<Expression> candidates) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is null || !candidates.Contains(node))
            {
                return base.Visit(node);
            }

            return node is ConstantExpression ? node : Expression.Constant(Evaluate(node), node.Type);
        }

        // An initializer's constructor call must stay a constructor call (new T { A = ... }), even when it has no
        // parameters to depend on; only the assigned values are evaluated.
        protected override Expression VisitMemberInit(MemberInitExpression node) =>
            node.Update(node.NewExpression, node.Bindings.Select(VisitMemberBinding));

        private static object? Evaluate(Expression node)
        {
            // Captured variables are fields of a closure constant; read them without compiling.
            if (node is MemberExpression { Expression: ConstantExpression { Value: var target } } member)
            {
                switch (member.Member)
                {
                    case FieldInfo field:
                        return field.GetValue(target);
                    case PropertyInfo property when property.GetIndexParameters().Length == 0:
                        return property.GetValue(target);
                }
            }

            // Interpreted, so it needs no dynamic code; boxing to object avoids a delegate type made for node.Type.
            return Expression.Lambda<Func<object?>>(Expression.Convert(node, typeof(object))).Compile(preferInterpretation: true)();
        }
    }
}

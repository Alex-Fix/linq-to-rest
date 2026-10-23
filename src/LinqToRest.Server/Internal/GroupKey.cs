using System.Linq.Expressions;

namespace LinqToRest.Server.Internal;

// Group keys of grouped queries: EF Core translates grouping by a member-init of these into GROUP BY. The server only
// closes one of its own classes over the key types; nothing is generated at run time.
internal sealed class GroupKey<T0>
{
    public T0 K0 { get; init; } = default!;
}

internal sealed class GroupKey<T0, T1>
{
    public T0 K0 { get; init; } = default!;

    public T1 K1 { get; init; } = default!;
}

internal sealed class GroupKey<T0, T1, T2>
{
    public T0 K0 { get; init; } = default!;

    public T1 K1 { get; init; } = default!;

    public T2 K2 { get; init; } = default!;
}

internal sealed class GroupKey<T0, T1, T2, T3>
{
    public T0 K0 { get; init; } = default!;

    public T1 K1 { get; init; } = default!;

    public T2 K2 { get; init; } = default!;

    public T3 K3 { get; init; } = default!;
}

internal static class GroupKeys
{
    private static readonly Type[] Definitions = [typeof(GroupKey<>), typeof(GroupKey<,>), typeof(GroupKey<,,>), typeof(GroupKey<,,,>)];

    /// <summary>
    /// <c>e =&gt; new GroupKey&lt;…&gt; { K0 = …, … }</c> over the key values, or a constant for one group of all rows.
    /// </summary>
    public static LambdaExpression Selector(IReadOnlyList<Expression> keys, ParameterExpression entity)
    {
        if (keys.Count == 0)
        {
            return Expression.Lambda(Expression.Constant(1), entity);
        }

        var type = Definitions[keys.Count - 1].MakeGenericType([.. keys.Select(key => key.Type)]);
        var bindings = keys.Select((key, index) => Expression.Bind(type.GetProperty($"K{index}")!, key));
        return Expression.Lambda(Expression.MemberInit(Expression.New(type), bindings), entity);
    }

    /// <summary>The n-th key of a group: <c>g.Key.K{n}</c>.</summary>
    public static Expression Key(Expression group, int index) =>
        Expression.Property(Expression.Property(group, "Key"), $"K{index}");
}

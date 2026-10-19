using System.Collections.Frozen;
using System.Reflection;

namespace LinqToRest.Server.Internal;

/// <summary>
/// The generic <see cref="Enumerable"/> method definitions the binder calls, resolved once: looking them up by name
/// (<c>Expression.Call(typeof(Enumerable), "Any", …)</c>) scans every overload on each call.
/// </summary>
internal static class EnumerableMethods
{
    public static readonly MethodInfo Any = Definition(new Func<IEnumerable<object>, bool>(Enumerable.Any));

    public static readonly MethodInfo AnyWhere = Definition(new Func<IEnumerable<object>, Func<object, bool>, bool>(Enumerable.Any));

    public static readonly MethodInfo All = Definition(new Func<IEnumerable<object>, Func<object, bool>, bool>(Enumerable.All));

    public static readonly MethodInfo Count = Definition(new Func<IEnumerable<object>, int>(Enumerable.Count));

    public static readonly MethodInfo CountWhere = Definition(new Func<IEnumerable<object>, Func<object, bool>, int>(Enumerable.Count));

    public static readonly MethodInfo Where = Definition(new Func<IEnumerable<object>, Func<object, bool>, IEnumerable<object>>(Enumerable.Where));

    public static readonly MethodInfo Select = Definition(new Func<IEnumerable<object>, Func<object, object>, IEnumerable<object>>(Enumerable.Select));

    public static readonly MethodInfo OrderBy = Definition(new Func<IEnumerable<object>, Func<object, object>, IOrderedEnumerable<object>>(Enumerable.OrderBy));

    public static readonly MethodInfo ThenBy = Definition(new Func<IOrderedEnumerable<object>, Func<object, object>, IOrderedEnumerable<object>>(Enumerable.ThenBy));

    public static readonly MethodInfo ToList = Definition(new Func<IEnumerable<object>, List<object>>(Enumerable.ToList));

    public static readonly MethodInfo Contains = Definition(new Func<IEnumerable<object>, object, bool>(Enumerable.Contains));

    /// <summary><c>Min&lt;TSource, TResult&gt;(source, selector)</c>.</summary>
    public static readonly MethodInfo Min = Selector(nameof(Enumerable.Min));

    /// <summary><c>Max&lt;TSource, TResult&gt;(source, selector)</c>.</summary>
    public static readonly MethodInfo Max = Selector(nameof(Enumerable.Max));

    /// <summary><c>Sum&lt;TSource&gt;(source, selector)</c> by the selector's result type.</summary>
    public static readonly FrozenDictionary<Type, MethodInfo> Sum = ByResultType(nameof(Enumerable.Sum));

    /// <summary><c>Average&lt;TSource&gt;(source, selector)</c> by the selector's result type.</summary>
    public static readonly FrozenDictionary<Type, MethodInfo> Average = ByResultType(nameof(Enumerable.Average));

    private static MethodInfo Definition(Delegate method) => method.Method.GetGenericMethodDefinition();

    private static IEnumerable<MethodInfo> WithSelector(string name) =>
        typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == name && method.IsGenericMethodDefinition
                && method.GetParameters() is [_, { ParameterType: { IsGenericType: true } selector }]
                && selector.GetGenericTypeDefinition() == typeof(Func<,>));

    private static MethodInfo Selector(string name) => WithSelector(name).Single(method => method.GetGenericArguments().Length == 2);

    private static FrozenDictionary<Type, MethodInfo> ByResultType(string name) =>
        WithSelector(name)
            .Where(method => method.GetGenericArguments().Length == 1)
            .ToFrozenDictionary(method => method.GetParameters()[1].ParameterType.GetGenericArguments()[1]);
}

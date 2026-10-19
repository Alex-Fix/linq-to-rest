namespace LinqToRest.Protocol;

/// <summary>The operator names of the query tree.</summary>
public static class QueryOperators
{
#pragma warning disable CS1591 // Operator names document themselves; see the design doc for semantics.
    public const string And = "and";
    public const string Or = "or";
    public const string Not = "not";
    public const string Eq = "eq";
    public const string Ne = "ne";
    public const string Gt = "gt";
    public const string Ge = "ge";
    public const string Lt = "lt";
    public const string Le = "le";
    public const string In = "in";
    public const string Add = "add";
    public const string Sub = "sub";
    public const string Mul = "mul";
    public const string Div = "div";
    public const string Mod = "mod";
    public const string Neg = "neg";
    public const string Contains = "contains";
    public const string StartsWith = "startsWith";
    public const string EndsWith = "endsWith";
    public const string ToLower = "toLower";
    public const string ToUpper = "toUpper";
    public const string Length = "length";
    public const string Trim = "trim";
    public const string Concat = "concat";
    public const string Year = "year";
    public const string Month = "month";
    public const string Day = "day";
    public const string Hour = "hour";
    public const string Minute = "minute";
    public const string Date = "date";
    public const string If = "if";
    public const string Coalesce = "coalesce";
    public const string Any = "any";
    public const string All = "all";
    public const string Count = "count";
    public const string Sum = "sum";
    public const string Min = "min";
    public const string Max = "max";
    public const string Avg = "avg";
    public const string Map = "map";
#pragma warning restore CS1591

    private static readonly Dictionary<string, (int Min, int Max)> CallArity = new(StringComparer.Ordinal)
    {
        [And] = (2, 64),
        [Or] = (2, 64),
        [Not] = (1, 1),
        [Eq] = (2, 2),
        [Ne] = (2, 2),
        [Gt] = (2, 2),
        [Ge] = (2, 2),
        [Lt] = (2, 2),
        [Le] = (2, 2),
        [In] = (2, 2),
        [Add] = (2, 2),
        [Sub] = (2, 2),
        [Mul] = (2, 2),
        [Div] = (2, 2),
        [Mod] = (2, 2),
        [Neg] = (1, 1),
        [Contains] = (2, 2),
        [StartsWith] = (2, 2),
        [EndsWith] = (2, 2),
        [ToLower] = (1, 1),
        [ToUpper] = (1, 1),
        [Length] = (1, 1),
        [Trim] = (1, 1),
        [Concat] = (2, 64),
        [Year] = (1, 1),
        [Month] = (1, 1),
        [Day] = (1, 1),
        [Hour] = (1, 1),
        [Minute] = (1, 1),
        [Date] = (1, 1),
        [If] = (3, 3),
        [Coalesce] = (2, 2),
    };

    private static readonly HashSet<string> LambdaOperators = new(StringComparer.Ordinal) { Any, All, Count, Sum, Min, Max, Avg, Map };

    /// <summary>Returns whether <paramref name="op"/> is a call operator and its allowed argument counts.</summary>
    /// <param name="op">The operator name.</param>
    /// <param name="min">The fewest arguments.</param>
    /// <param name="max">The most arguments.</param>
    /// <returns><see langword="true"/> for call operators.</returns>
    public static bool TryGetCallArity(string op, out int min, out int max)
    {
        var found = CallArity.TryGetValue(op, out var arity);
        (min, max) = arity;
        return found;
    }

    /// <summary>Returns whether <paramref name="op"/> is a collection lambda operator.</summary>
    /// <param name="op">The operator name.</param>
    /// <returns><see langword="true"/> for any, all, count, sum, min, max, avg and map.</returns>
    public static bool IsLambda(string op) => LambdaOperators.Contains(op);
}

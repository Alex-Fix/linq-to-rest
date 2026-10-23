using System.Linq.Expressions;
using LinqToRest.Protocol;

namespace LinqToRest.Client.Translation;

/// <summary>A LINQ query translated into the parts of a <see cref="QueryRequest"/>.</summary>
internal sealed class TranslatedQuery(ClientSet type, NodeTranslator translator)
{
    public ClientSet Type { get; } = type;

    /// <summary>Gets the translator of this query's lambdas, which keeps lambda variable names unique across the query.</summary>
    public NodeTranslator Translator { get; } = translator;

    public List<QueryNode> Filters { get; } = [];

    public List<OrderByItem> OrderBy { get; } = [];

    public List<MemberNode> Include { get; } = [];

    public QueryNode? Select { get; set; }

    public bool HasSelect { get; set; }

    /// <summary>Gets or sets the <c>Select</c> lambda, which the projection reader follows to read the results.</summary>
    public LambdaExpression? Selector { get; set; }

    public int? Skip { get; set; }

    public int? Take { get; set; }

    public string? After { get; set; }

    public bool NoTracking { get; set; }

    /// <summary>Gets or sets the group keys of a grouped query (<c>GroupBy(…).Select(…)</c>), or <see langword="null"/>.</summary>
    public List<MemberNode>? GroupBy { get; set; }

    /// <summary>Gets the filters on groups: <c>Where</c> after the group <c>Select</c>.</summary>
    public List<QueryNode> Having { get; } = [];

    public bool IsGrouped => GroupBy is not null;

    public QueryRequest ToRequest(PageRequest page, bool count = false, QueryNode? select = null) => new()
    {
        Filter = And(Filters),
        OrderBy = OrderBy,
        Include = Include,
        Select = select ?? Select,
        Page = page,
        Count = count,
        GroupBy = GroupBy,
        Having = And(Having),
    };

    private static QueryNode? And(List<QueryNode> nodes) => nodes.Count switch
    {
        0 => null,
        1 => nodes[0],
        _ => new CallNode(QueryOperators.And, nodes),
    };

    public static TranslatedQuery Translate(Expression expression)
    {
        var calls = new List<MethodCallExpression>();
        while (expression is MethodCallExpression call)
        {
            calls.Insert(0, call);
            expression = call.Arguments[0];
        }

        if (expression is not ConstantExpression { Value: IRestSet set })
        {
            throw new NotSupportedException("LinqToRest queries must start from a RestSet of a RestDataContext.");
        }

        var translator = new NodeTranslator(set.Type);
        var query = new TranslatedQuery(set.Type, translator);
        var phase = Phase.Compose;
        var selected = false;
        HashSet<string>? fields = null;
        foreach (var call in calls)
        {
            var name = call.Method.Name;
            var declaring = call.Method.DeclaringType;
            if (declaring != typeof(Queryable) && declaring != typeof(RestQueryableExtensions))
            {
                throw Unsupported(name);
            }

            // Filtering, includes and sorting come before Select; paging may come before or after it. A grouped query
            // filters and sorts its groups after the group Select, over the selected fields.
            var composing = phase == Phase.Compose && !selected && !query.IsGrouped;
            var composingGroups = phase == Phase.Compose && selected && query.IsGrouped;
            switch (name)
            {
                case nameof(Queryable.GroupBy) when composing && call.Arguments.Count == 2:
                    query.GroupBy = translator.GroupKeys(Lambda(call.Arguments[1]));
                    break;
                case nameof(Queryable.Select) when query.IsGrouped && !selected && call.Arguments.Count == 2:
                    var groupSelector = Lambda(call.Arguments[1]);
                    query.Select = translator.GroupSelect(groupSelector);
                    (query.HasSelect, query.Selector, selected) = (true, groupSelector, true);
                    fields = FieldNames(groupSelector);
                    break;
                case nameof(Queryable.Where) when composingGroups:
                    query.Having.Add(translator.Having(Lambda(call.Arguments[1]), fields!));
                    break;
                case nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) when composingGroups && query.OrderBy.Count == 0:
                case nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending) when composingGroups && query.OrderBy.Count > 0:
                    query.OrderBy.Add(new OrderByItem(translator.FieldPath(Lambda(call.Arguments[1]), fields!), name.EndsWith("Descending", StringComparison.Ordinal)));
                    break;
                case nameof(Queryable.Where) when composing:
                    query.Filters.Add(translator.Predicate(Lambda(call.Arguments[1])));
                    break;
                case nameof(RestQueryableExtensions.Include) when composing:
                    query.Include.Add(translator.Path(Lambda(call.Arguments[1]), navigation: true));
                    break;
                case nameof(RestQueryableExtensions.AsNoTracking):
                    query.NoTracking = true;
                    break;
                case nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending) when composing && query.OrderBy.Count == 0:
                case nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending) when composing && query.OrderBy.Count > 0:
                    query.OrderBy.Add(new OrderByItem(translator.Path(Lambda(call.Arguments[1]), navigation: false), name.EndsWith("Descending", StringComparison.Ordinal)));
                    break;
                case nameof(RestQueryableExtensions.After) when phase == Phase.Compose && !query.IsGrouped:
                    query.After = (string)Constant(call.Arguments[1])!;
                    phase = Phase.Paged;
                    break;
                case nameof(Queryable.Skip) when phase == Phase.Compose:
                    query.Skip = (int)Constant(call.Arguments[1])!;
                    phase = Phase.Paged;
                    break;
                case nameof(Queryable.Take) when phase is Phase.Compose or Phase.Paged:
                    query.Take = (int)Constant(call.Arguments[1])!;
                    phase = Phase.Taken;
                    break;
                case nameof(Queryable.Select) when !selected && call.Arguments.Count == 2 && Lambda(call.Arguments[1]).Parameters.Count == 1:
                    query.Select = translator.Select(Lambda(call.Arguments[1]));
                    query.HasSelect = query.Select is not null;
                    query.Selector = query.HasSelect ? Lambda(call.Arguments[1]) : null;
                    selected = true;
                    break;
                default:
                    throw Unsupported(name, phase);
            }
        }

        if (query is { HasSelect: true, Include.Count: > 0 })
        {
            throw new NotSupportedException("Include can't be combined with Select; select the navigation instead.");
        }

        if (query is { IsGrouped: true, HasSelect: false })
        {
            throw new NotSupportedException("GroupBy needs a Select over the groups, such as g => new { g.Key, Count = g.Count() }; LinqToRest doesn't return grouped rows.");
        }

        return query;
    }

    /// <summary>The wire names of the fields a group Select projects, which Where and OrderBy after it may use.</summary>
    private static HashSet<string> FieldNames(LambdaExpression selector)
    {
        IEnumerable<string> names = selector.Body switch
        {
            NewExpression { Members: { } members } => members.Select(member => member.Name),
            NewExpression { Constructor: { } constructor } => constructor.GetParameters().Select(parameter => parameter.Name!),
            MemberInitExpression initializer => initializer.Bindings.Select(binding => binding.Member.Name),
            _ => [],
        };
        return [.. names.Select(name => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(name))];
    }

    private static NotSupportedException Unsupported(string name, Phase? phase = null) => phase switch
    {
        null => new NotSupportedException($"The LINQ operator '{name}' isn't supported by LinqToRest."),
        _ => new NotSupportedException($"'{name}' isn't supported at this point. LinqToRest queries are Where/Include/OrderBy/ThenBy, then Select, with After or Skip, then Take, anywhere after the sort; each at most once except Where."),
    };

    private static LambdaExpression Lambda(Expression expression) =>
        (LambdaExpression)(expression is UnaryExpression { NodeType: ExpressionType.Quote } quote ? quote.Operand : expression);

    private static object? Constant(Expression expression) =>
        Evaluator.PartialEval(expression) is ConstantExpression constant
            ? constant.Value
            : throw new NotSupportedException($"'{expression}' must be a value, not depend on the query.");

    private enum Phase
    {
        Compose,
        Paged,
        Taken,
    }
}

internal interface IRestSet
{
    ClientSet Type { get; }
}

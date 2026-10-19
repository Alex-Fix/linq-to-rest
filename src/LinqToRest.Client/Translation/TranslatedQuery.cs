using System.Linq.Expressions;
using LinqToRest.Modeling;
using LinqToRest.Protocol;

namespace LinqToRest.Client.Translation;

/// <summary>A LINQ query translated into the parts of a <see cref="QueryRequest"/>.</summary>
internal sealed class TranslatedQuery(DtoType type)
{
    public DtoType Type { get; } = type;

    public List<QueryNode> Filters { get; } = [];

    public List<OrderByItem> OrderBy { get; } = [];

    public List<MemberNode> Include { get; } = [];

    public QueryNode? Select { get; set; }

    public bool HasSelect { get; set; }

    public int? Skip { get; set; }

    public int? Take { get; set; }

    public string? After { get; set; }

    public bool NoTracking { get; set; }

    public QueryRequest ToRequest(PageRequest page, bool count = false, QueryNode? select = null) => new()
    {
        Filter = Filters.Count switch
        {
            0 => null,
            1 => Filters[0],
            _ => new CallNode(QueryOperators.And, Filters),
        },
        OrderBy = OrderBy,
        Include = Include,
        Select = select ?? Select,
        Page = page,
        Count = count,
    };

    public static TranslatedQuery Translate(Expression expression)
    {
        var calls = new List<MethodCallExpression>();
        while (expression is MethodCallExpression call)
        {
            calls.Insert(0, call);
            expression = call.Arguments[0];
        }

        if (expression is not ConstantExpression { Value: IDtoSet set })
        {
            throw new NotSupportedException("LinqToRest queries must start from a DtoSet of a RestDataContext.");
        }

        var query = new TranslatedQuery(set.Type);
        var translator = new NodeTranslator(set.Type);
        var phase = Phase.Compose;
        var selected = false;
        foreach (var call in calls)
        {
            var name = call.Method.Name;
            var declaring = call.Method.DeclaringType;
            if (declaring != typeof(Queryable) && declaring != typeof(RestQueryableExtensions))
            {
                throw Unsupported(name);
            }

            // Filtering, includes and sorting come before Select; paging may come before or after it.
            var composing = phase == Phase.Compose && !selected;
            switch (name)
            {
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
                case nameof(RestQueryableExtensions.After) when phase == Phase.Compose:
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

        return query;
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

internal interface IDtoSet
{
    DtoType Type { get; }
}

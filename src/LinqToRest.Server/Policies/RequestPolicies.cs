using System.Linq.Expressions;
using LinqToRest.Server.Mapping;

namespace LinqToRest.Server.Policies;

/// <summary>The policies of one request, resolved and evaluated at most once per set.</summary>
internal sealed class RequestPolicies(PolicyContext context)
{
    private readonly Dictionary<SetMapping, ISetPolicy?> _policies = [];
    private readonly Dictionary<SetMapping, LambdaExpression?> _filters = [];

    public PolicyContext Context { get; } = context;

    public ISetPolicy? Get(SetMapping mapping)
    {
        if (!_policies.TryGetValue(mapping, out var policy))
        {
            policy = (ISetPolicy?)Context.Services.GetService(mapping.PolicyType);
            _policies.Add(mapping, policy);
        }

        return policy;
    }

    public LambdaExpression? Filter(SetMapping mapping)
    {
        if (!_filters.TryGetValue(mapping, out var filter))
        {
            filter = Get(mapping)?.GetFilter(Context);
            _filters.Add(mapping, filter);
        }

        return filter;
    }

    public ValueTask<bool> CanAsync(SetMapping mapping, SetOperation operation) =>
        Get(mapping)?.CanAsync(operation, Context) ?? ValueTask.FromResult(true);
}

using System.Linq.Expressions;
using RestToLinq.Server.Mapping;

namespace RestToLinq.Server.Policies;

/// <summary>The policies of one request, resolved and evaluated at most once per set.</summary>
internal sealed class RequestPolicies(PolicyContext context)
{
    private readonly Dictionary<SetMapping, IDtoSetPolicy?> _policies = [];
    private readonly Dictionary<SetMapping, LambdaExpression?> _filters = [];

    public PolicyContext Context { get; } = context;

    public IDtoSetPolicy? Get(SetMapping mapping)
    {
        if (!_policies.TryGetValue(mapping, out var policy))
        {
            policy = (IDtoSetPolicy?)Context.Services.GetService(mapping.PolicyType);
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

    public ValueTask<bool> CanAsync(SetMapping mapping, DtoOperation operation) =>
        Get(mapping)?.CanAsync(operation, Context) ?? ValueTask.FromResult(true);
}

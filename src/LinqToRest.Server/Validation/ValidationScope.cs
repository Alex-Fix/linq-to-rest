using LinqToRest.Protocol;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;

namespace LinqToRest.Server.Validation;

/// <summary>One operation being validated: what it sends, and the issue list its errors go to.</summary>
internal sealed class ValidationScope(BatchOperation operation, string location, SetMapping mapping, PolicyContext request, List<ValidationIssue> issues)
{
    public SetMapping Mapping { get; } = mapping;

    public PolicyContext Request { get; } = request;

    public SetOperation Operation { get; } = operation.Op == BatchOperationKind.Insert ? SetOperation.Insert : SetOperation.Update;

    public bool IsSent(SetProperty member) => operation.Values?.ContainsKey(member.Name) == true;

    public void Add(SetProperty? member, string code) => issues.Add(new ValidationIssue
    {
        Operation = operation.Id,
        Location = member is null ? location : $"{location}/values/{member.Name}",
        Member = member?.Name,
        Code = code,
    });
}

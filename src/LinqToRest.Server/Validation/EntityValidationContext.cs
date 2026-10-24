using System.Linq.Expressions;
using System.Reflection;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Sets;

namespace LinqToRest.Server.Validation;

/// <summary>The row an <see cref="IEntityValidator{TEntity}"/> checks, and where its errors go.</summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class EntityValidationContext<TEntity>
    where TEntity : class
{
    private readonly ValidationScope _scope;

    internal EntityValidationContext(TEntity entity, ValidationScope scope)
    {
        Entity = entity;
        _scope = scope;
    }

    /// <summary>Gets the row, with the batch's values applied.</summary>
    public TEntity Entity { get; }

    /// <summary>Gets the operation: <see cref="SetOperation.Insert"/> or <see cref="SetOperation.Update"/>.</summary>
    public SetOperation Operation => _scope.Operation;

    /// <summary>Gets the request (user, services).</summary>
    public PolicyContext Request => _scope.Request;

    /// <summary>
    /// Returns whether the batch sets the member: always on inserts, and on updates when it sends a value. A member of
    /// a value is set whenever its value is (values are written whole).
    /// </summary>
    /// <param name="member">An exposed property, <c>e =&gt; e.Sku</c>, or a member of a value, <c>e =&gt; e.Address.City</c>.</param>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <returns>Whether the member is set by this operation.</returns>
    public bool IsModified<TMember>(Expression<Func<TEntity, TMember>> member) =>
        Operation == SetOperation.Insert || _scope.IsSent(Find(member).Property);

    /// <summary>Reports an error on a member; the client sees it next to that member.</summary>
    /// <param name="member">An exposed property, <c>e =&gt; e.Sku</c>, or a member of a value, <c>e =&gt; e.Address.City</c>.</param>
    /// <param name="code">The error code: <c>snake_case</c>, at most 64 characters, not a built-in code.</param>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <exception cref="InvalidOperationException">The member isn't exposed or the code isn't valid (a server bug).</exception>
    public void AddError<TMember>(Expression<Func<TEntity, TMember>> member, string code) => _scope.Add(Find(member).Path, Checked(code));

    /// <summary>Reports an error on the whole row.</summary>
    /// <param name="code">The error code: <c>snake_case</c>, at most 64 characters, not a built-in code.</param>
    /// <exception cref="InvalidOperationException">The code isn't valid (a server bug).</exception>
    public void AddError(string code) => _scope.Add((string?)null, Checked(code));

    private static string Checked(string code) =>
        ValidationCodes.IsValid(code) ? code : throw new InvalidOperationException("A validator reported an invalid error code. Codes are snake_case, at most 64 characters, and not built-in codes.");

    // The set's property and the wire path to the member (size/weight/unit inside a value).
    private (SetProperty Property, string Path) Find<TMember>(Expression<Func<TEntity, TMember>> member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var chain = MemberAccess.GetPropertyPath(member, nameof(member));
        var property = _scope.Mapping.Properties.FirstOrDefault(exposed => exposed.EntityProperty == chain[0]) ?? throw NotExposed(chain[0]);
        var (current, path) = (property, property.Name);
        foreach (var info in chain.Skip(1))
        {
            current = current.ValueType?.Properties.FirstOrDefault(exposed => exposed.EntityProperty == info) ?? throw NotExposed(info);
            path += "/" + current.Name;
        }

        return (property, path);
    }

    private InvalidOperationException NotExposed(PropertyInfo property) =>
        new($"A validator of the set '{_scope.Mapping.Name}' named '{property.Name}', which the set doesn't expose.");
}

using System.Linq.Expressions;
using LinqToRest.Modeling;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Validation;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.Sets;

/// <summary>
/// Configures how an EF Core entity is exposed as a set. Nothing is exposed unless configured here; keys (and a
/// concurrency token, if configured) are exposed automatically.
/// </summary>
/// <typeparam name="TEntity">The entity type.</typeparam>
public sealed class EntitySetBuilder<TEntity>
    where TEntity : class
{
    internal EntitySetBuilder()
    {
    }

    internal SetDefinition Definition { get; } = new(typeof(TEntity));

    /// <summary>Sets the set name used in URLs and batches: a lowercase letter followed by letters or digits.</summary>
    /// <param name="name">The set name, for example <c>products</c>.</param>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> ToSet(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Definition.Name = name;
        return this;
    }

    /// <summary>Sets the name clients generate for the set's rows. Default: the entity type's name.</summary>
    /// <param name="name">The type name: an uppercase letter followed by letters or digits.</param>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> HasTypeName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Definition.TypeName = name;
        return this;
    }

    /// <summary>
    /// Sets the query rows come from, for example to apply EF Core query filters or a default include.
    /// Default: <c>db.Set&lt;TEntity&gt;()</c>.
    /// </summary>
    /// <param name="source">The source query.</param>
    /// <typeparam name="TContext">The DbContext type registered with <c>UseDbContext</c>.</typeparam>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> FromSource<TContext>(Expression<Func<TContext, IQueryable<TEntity>>> source)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(source);
        Definition.Source = source;
        Definition.SourceContextType = typeof(TContext);
        return this;
    }

    /// <summary>
    /// Sets the key: <c>e =&gt; e.Id</c> or <c>e =&gt; new { e.A, e.B }</c>. Default: the EF Core primary key. Key
    /// properties are exposed, sortable and never writable.
    /// </summary>
    /// <param name="key">The key properties.</param>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> HasKey<TKey>(Expression<Func<TEntity, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Definition.Key = MemberAccess.GetProperties(key, nameof(key));
        return this;
    }

    /// <summary>
    /// Exposes the concurrency token. Updates and deletes must send its current value. It must be a concurrency token
    /// in the EF Core model (<c>IsConcurrencyToken()</c> or <c>IsRowVersion()</c>) and is never writable.
    /// </summary>
    /// <param name="token">The token property.</param>
    /// <typeparam name="TToken">The token type.</typeparam>
    /// <returns>A builder for the token's wire name.</returns>
    public EntityPropertyBuilder HasConcurrencyToken<TToken>(Expression<Func<TEntity, TToken>> token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var member = Definition.GetProperty(MemberAccess.GetProperty(token, nameof(token)), token);
        Definition.ConcurrencyToken = member;
        return new EntityPropertyBuilder(member);
    }

    /// <summary>
    /// Exposes an EF Core-mapped scalar or primitive-collection property: <c>e =&gt; e.Name</c>. Its wire name is the
    /// camelCase property name. It's filterable, and writable when the set allows inserts or updates.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <typeparam name="TProperty">The property type.</typeparam>
    /// <returns>A builder for the property's options.</returns>
    public EntityPropertyBuilder Property<TProperty>(Expression<Func<TEntity, TProperty>> property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new EntityPropertyBuilder(Definition.GetProperty(MemberAccess.GetProperty(property, nameof(property)), property));
    }

    /// <summary>
    /// Exposes an EF Core complex or owned property (<c>OwnsOne</c>, also with <c>ToJson()</c>) whose type is a
    /// configured value type: <c>e =&gt; e.ShippingAddress</c>. Its wire name is the camelCase property name. It's
    /// filterable through its members, and writable as a whole when the set allows inserts or updates.
    /// </summary>
    /// <param name="value">The complex or owned property.</param>
    /// <typeparam name="TValue">The value type, configured with an <see cref="IValueTypeConfiguration{TValue}"/>.</typeparam>
    /// <returns>A builder for the property's options.</returns>
    public ValueUseBuilder Value<TValue>(Expression<Func<TEntity, TValue?>> value)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ValueUseBuilder(Definition.GetValue(MemberAccess.GetProperty(value, nameof(value)), value));
    }

    /// <summary>
    /// Exposes a read-only value computed from the entity, such as <c>e =&gt; e.Category!.Name</c>. The expression
    /// must translate on EF Core. It reads what it references without the policy filters of other sets, so keep it
    /// within what this set may show.
    /// </summary>
    /// <param name="name">The wire name: a lowercase letter followed by letters or digits.</param>
    /// <param name="value">The value.</param>
    /// <typeparam name="TValue">The value type: a supported scalar or a collection of one.</typeparam>
    /// <returns>A builder for the value's options.</returns>
    public ComputedPropertyBuilder Computed<TValue>(string name, Expression<Func<TEntity, TValue>> value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        var member = new MemberDefinition(null, name, value);
        Definition.Members.Add(member);
        return new ComputedPropertyBuilder(member);
    }

    /// <summary>Exposes a reference navigation to another configured set: <c>e =&gt; e.Category</c>.</summary>
    /// <param name="navigation">The EF Core navigation.</param>
    /// <typeparam name="TTarget">The target entity type.</typeparam>
    /// <returns>A builder for the navigation's foreign key and name.</returns>
    public ReferenceNavigationBuilder<TEntity> HasOne<TTarget>(Expression<Func<TEntity, TTarget?>> navigation)
        where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var definition = new NavigationDefinition(MemberAccess.GetProperty(navigation, nameof(navigation)), navigation, typeof(TTarget), isCollection: false);
        Definition.Navigations.Add(definition);
        return new ReferenceNavigationBuilder<TEntity>(definition);
    }

    /// <summary>Exposes a collection navigation to another configured set: <c>e =&gt; e.Lines</c>.</summary>
    /// <param name="navigation">The EF Core navigation.</param>
    /// <typeparam name="TTarget">The target entity type.</typeparam>
    /// <returns>A builder for the navigation's name.</returns>
    public CollectionNavigationBuilder HasMany<TTarget>(Expression<Func<TEntity, IEnumerable<TTarget>>> navigation)
        where TTarget : class
    {
        ArgumentNullException.ThrowIfNull(navigation);
        var definition = new NavigationDefinition(MemberAccess.GetProperty(navigation, nameof(navigation)), navigation, typeof(TTarget), isCollection: true);
        Definition.Navigations.Add(definition);
        return new CollectionNavigationBuilder(definition);
    }

    /// <summary>Allows writes through <c>$batch</c>. Default: none, the set is read-only.</summary>
    /// <param name="operations">The operations to allow.</param>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> AllowWrites(WriteOperations operations)
    {
        Definition.Writes = operations;
        return this;
    }

    /// <summary>
    /// Adds a server-only rule on one member: inserts and updates that send the member fail with <paramref name="code"/>
    /// when <paramref name="rule"/> returns <see langword="false"/>. Not exported to clients.
    /// </summary>
    /// <param name="member">The member, exposed with <c>Property(...)</c>: <c>e =&gt; e.Price</c>.</param>
    /// <param name="rule">Returns whether the value is valid.</param>
    /// <param name="code">The error code: <c>snake_case</c>, at most 64 characters, not a built-in code.</param>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> Must<TMember>(Expression<Func<TEntity, TMember>> member, Func<TMember, bool> rule, string code)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var property = MemberAccess.GetProperty(member, nameof(member));
        Definition.Rules.Add(new RuleDefinition.Custom(property, entity => rule((TMember)property.GetValue(entity)!), ValidationCodes.Check(code), OnlyWhenSent: true));
        return this;
    }

    /// <summary>
    /// Adds a server-only rule on the whole row, checked on every insert and update: it fails with
    /// <paramref name="code"/> when <paramref name="rule"/> returns <see langword="false"/>. Not exported to clients.
    /// </summary>
    /// <param name="rule">Returns whether the row is valid.</param>
    /// <param name="code">The error code: <c>snake_case</c>, at most 64 characters, not a built-in code.</param>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> Must(Func<TEntity, bool> rule, string code)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Definition.Rules.Add(new RuleDefinition.Custom(null, entity => rule((TEntity)entity), ValidationCodes.Check(code), OnlyWhenSent: false));
        return this;
    }

    /// <summary>
    /// Adds a server-only rule on the whole row, checked on every insert and update and reported against
    /// <paramref name="member"/> (for example the later of two dates). Not exported to clients.
    /// </summary>
    /// <param name="rule">Returns whether the row is valid.</param>
    /// <param name="code">The error code: <c>snake_case</c>, at most 64 characters, not a built-in code.</param>
    /// <param name="member">The exposed member the error points at.</param>
    /// <typeparam name="TMember">The member type.</typeparam>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> Must<TMember>(Func<TEntity, bool> rule, string code, Expression<Func<TEntity, TMember>> member)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var property = MemberAccess.GetProperty(member, nameof(member));
        Definition.Rules.Add(new RuleDefinition.Custom(property, entity => rule((TEntity)entity), ValidationCodes.Check(code), OnlyWhenSent: false));
        return this;
    }

    /// <summary>
    /// Adds a validator for inserts and updates, created once per request from its services. Not exported to
    /// clients.
    /// </summary>
    /// <typeparam name="TValidator">The validator.</typeparam>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> ValidateWith<TValidator>()
        where TValidator : class, IEntityValidator<TEntity>
    {
        if (!Definition.Validators.Contains(typeof(TValidator)))
        {
            Definition.Validators.Add(typeof(TValidator));
        }

        return this;
    }

    /// <summary>Configures pagination. Default: offset and cursor, 50 per page, at most 200, skip at most 10 000.</summary>
    /// <param name="configure">Configures the pagination.</param>
    /// <returns>The same builder.</returns>
    public EntitySetBuilder<TEntity> HasPagination(Action<SetPaginationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Definition.Pagination);
        return this;
    }
}

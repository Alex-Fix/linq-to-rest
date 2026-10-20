using System.Collections;
using System.Linq.Expressions;

namespace LinqToRest.Client;

/// <summary>A query composed from a <see cref="RestSet{TEntity}"/>.</summary>
internal sealed class RestQueryable<T>(RestQueryProvider provider, Expression expression) : IOrderedQueryable<T>
{
    public Type ElementType => typeof(T);

    public Expression Expression { get; } = expression;

    public IQueryProvider Provider { get; } = provider;

    public IEnumerator<T> GetEnumerator() => throw RestQueryProvider.Synchronous();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

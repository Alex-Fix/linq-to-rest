using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using LinqToRest.Client.Translation;
using LinqToRest.Protocol;

namespace LinqToRest.Client;

/// <summary>LinqToRest query operators: includes, cursors and the async terminal operators.</summary>
public static class RestQueryableExtensions
{
    private static readonly MethodInfo IncludeMethod = typeof(RestQueryableExtensions).GetMethod(nameof(Include))!;
    private static readonly MethodInfo AfterMethod = typeof(RestQueryableExtensions).GetMethod(nameof(After))!;
    private static readonly MethodInfo AsNoTrackingMethod = typeof(RestQueryableExtensions).GetMethod(nameof(AsNoTracking))!;
    private static readonly NewNode Nothing = new([]);

    /// <summary>Loads a navigation (or a navigation of a navigation, <c>d =&gt; d.Category.Products</c>) with the full entities.</summary>
    /// <param name="source">The query.</param>
    /// <param name="navigation">The navigation.</param>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <typeparam name="TProperty">The navigation type.</typeparam>
    /// <returns>The query.</returns>
    public static IQueryable<TEntity> Include<TEntity, TProperty>(this IQueryable<TEntity> source, Expression<Func<TEntity, TProperty>> navigation)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(navigation);
        return Provider(source).CreateQuery<TEntity>(
            Expression.Call(null, IncludeMethod.MakeGenericMethod(typeof(TEntity), typeof(TProperty)), source.Expression, Expression.Quote(navigation)));
    }

    /// <summary>Continues keyset pagination after a cursor from <see cref="CursorPage{T}.NextCursor"/>.</summary>
    /// <param name="source">The query, with the same sort as the page the cursor came from.</param>
    /// <param name="cursor">The cursor.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The query.</returns>
    public static IQueryable<T> After<T>(this IQueryable<T> source, string cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Provider(source).CreateQuery<T>(Expression.Call(null, AfterMethod.MakeGenericMethod(typeof(T)), source.Expression, Expression.Constant(cursor)));
    }

    /// <summary>Returns the entities without tracking them.</summary>
    /// <param name="source">The query.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The query.</returns>
    public static IQueryable<T> AsNoTracking<T>(this IQueryable<T> source) =>
        Provider(source).CreateQuery<T>(Expression.Call(null, AsNoTrackingMethod.MakeGenericMethod(typeof(T)), source.Expression));

    /// <summary>
    /// Runs the query and returns one page. Requires <c>Take</c>, so a server page size can never be mistaken for
    /// all rows; use <see cref="AsAsyncEnumerable{T}"/> to read everything.
    /// </summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The items.</returns>
    public static async Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var (provider, query) = Translate(source);
        var take = query.Take ?? throw new NotSupportedException("Add Take(n) before ToListAsync, or use AsAsyncEnumerable() to read every row.");
        var response = await provider.SendAsync<T>(query, query.ToRequest(RestQueryProvider.Page(query, take)), cancellationToken).ConfigureAwait(false);
        return response.Items;
    }

    /// <summary>Runs the query and returns one page as an array. Requires <c>Take</c>.</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The items.</returns>
    public static async Task<T[]> ToArrayAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        [.. await source.ToListAsync(cancellationToken).ConfigureAwait(false)];

    /// <summary>Returns one keyset page and the cursor of the next one.</summary>
    /// <param name="source">The query; <c>Take</c> sets the page size (default: the set's).</param>
    /// <param name="includeTotalCount">Whether to also count all matching rows.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The page.</returns>
    public static async Task<CursorPage<T>> ToCursorPageAsync<T>(this IQueryable<T> source, bool includeTotalCount = false, CancellationToken cancellationToken = default)
    {
        var (provider, query) = Translate(source);
        var page = RestQueryProvider.Cursor(query, query.After, query.Take ?? query.Type.Pagination.DefaultPageSize);
        var response = await provider.SendAsync<T>(query, query.ToRequest(page, includeTotalCount), cancellationToken).ConfigureAwait(false);
        return new CursorPage<T>(response.Items, response.NextCursor, response.TotalCount);
    }

    /// <summary>Streams every matching row, following cursor pages (<c>Take</c> caps the total).</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The rows.</returns>
    public static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IQueryable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (provider, query) = Translate(source);
        var remaining = query.Take ?? int.MaxValue;
        var after = query.After;
        while (remaining > 0)
        {
            var page = RestQueryProvider.Cursor(query, after, Math.Min(remaining, query.Type.Pagination.DefaultPageSize));
            var response = await provider.SendAsync<T>(query, query.ToRequest(page), cancellationToken).ConfigureAwait(false);
            foreach (var item in response.Items)
            {
                yield return item;
            }

            remaining -= response.Items.Count;
            after = response.NextCursor;
            if (after is null)
            {
                yield break;
            }
        }
    }

    /// <summary>Returns the first row.</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The row.</returns>
    /// <exception cref="InvalidOperationException">No row matches.</exception>
    public static async Task<T> FirstAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var items = await TakeAsync(source, 1, cancellationToken).ConfigureAwait(false);
        return items.Count > 0 ? items[0] : throw new InvalidOperationException("The query returned no rows.");
    }

    /// <summary>Returns the first row matching <paramref name="predicate"/>.</summary>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The row.</returns>
    public static Task<T> FirstAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        source.Where(predicate).FirstAsync(cancellationToken);

    /// <summary>Returns the first row, or the default value when there's none.</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The row or default.</returns>
    public static async Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var items = await TakeAsync(source, 1, cancellationToken).ConfigureAwait(false);
        return items.Count > 0 ? items[0] : default;
    }

    /// <summary>Returns the first row matching <paramref name="predicate"/>, or the default value.</summary>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The row or default.</returns>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        source.Where(predicate).FirstOrDefaultAsync(cancellationToken);

    /// <summary>Returns the only row.</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The row.</returns>
    /// <exception cref="InvalidOperationException">No row or more than one row matches.</exception>
    public static async Task<T> SingleAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var items = await TakeAsync(source, 2, cancellationToken).ConfigureAwait(false);
        return items.Count == 1 ? items[0] : throw new InvalidOperationException(items.Count == 0 ? "The query returned no rows." : "The query returned more than one row.");
    }

    /// <summary>Returns the only row matching <paramref name="predicate"/>.</summary>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The row.</returns>
    public static Task<T> SingleAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        source.Where(predicate).SingleAsync(cancellationToken);

    /// <summary>Returns the only row, or the default value when there's none.</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The row or default.</returns>
    /// <exception cref="InvalidOperationException">More than one row matches.</exception>
    public static async Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var items = await TakeAsync(source, 2, cancellationToken).ConfigureAwait(false);
        return items.Count switch
        {
            0 => default,
            1 => items[0],
            _ => throw new InvalidOperationException("The query returned more than one row."),
        };
    }

    /// <summary>Returns the only row matching <paramref name="predicate"/>, or the default value.</summary>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The row or default.</returns>
    public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        source.Where(predicate).SingleOrDefaultAsync(cancellationToken);

    /// <summary>Counts the matching rows (after Skip/Take, like LINQ).</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The count.</returns>
    public static async Task<int> CountAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        checked((int)await source.LongCountAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Counts the rows matching <paramref name="predicate"/>.</summary>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The count.</returns>
    public static Task<int> CountAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        source.Where(predicate).CountAsync(cancellationToken);

    /// <summary>Counts the matching rows (after Skip/Take, like LINQ).</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The count.</returns>
    public static async Task<long> LongCountAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var (provider, query) = Translate(source);
        if (query.After is not null)
        {
            throw new NotSupportedException("Counting after a cursor isn't supported; count the query without After.");
        }

        var skip = query.Skip ?? 0;
        query.Skip = null;
        var response = await provider.SendAsync<object>(query, query.ToRequest(RestQueryProvider.Page(query, 0), count: true, select: Nothing), cancellationToken).ConfigureAwait(false);
        var count = Math.Max(0, response.TotalCount!.Value - skip);
        return query.Take is { } take ? Math.Min(count, take) : count;
    }

    /// <summary>Counts the rows matching <paramref name="predicate"/>.</summary>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The count.</returns>
    public static Task<long> LongCountAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        source.Where(predicate).LongCountAsync(cancellationToken);

    /// <summary>Returns whether any row matches.</summary>
    /// <param name="source">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>Whether a row exists.</returns>
    public static async Task<bool> AnyAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var (provider, query) = Translate(source);
        var page = RestQueryProvider.Page(query, query.Take is { } take ? Math.Min(take, 1) : 1);
        var response = await provider.SendAsync<object>(query, query.ToRequest(page, select: Nothing), cancellationToken).ConfigureAwait(false);
        return response.Items.Count > 0;
    }

    /// <summary>Returns whether any row matches <paramref name="predicate"/>.</summary>
    /// <param name="source">The query.</param>
    /// <param name="predicate">The predicate.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>Whether a row exists.</returns>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        source.Where(predicate).AnyAsync(cancellationToken);

    private static async Task<List<T>> TakeAsync<T>(IQueryable<T> source, int limit, CancellationToken cancellationToken)
    {
        var (provider, query) = Translate(source);
        var page = RestQueryProvider.Page(query, query.Take is { } take ? Math.Min(take, limit) : limit);
        var response = await provider.SendAsync<T>(query, query.ToRequest(page), cancellationToken).ConfigureAwait(false);
        return response.Items;
    }

    private static (RestQueryProvider Provider, TranslatedQuery Query) Translate<T>(IQueryable<T> source) =>
        (Provider(source), TranslatedQuery.Translate(source.Expression));

    private static RestQueryProvider Provider<T>(IQueryable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Provider as RestQueryProvider
            ?? throw new InvalidOperationException("This operator only works on queries that start from a LinqToRest RestSet.");
    }
}

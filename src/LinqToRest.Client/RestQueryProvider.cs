using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Text.Json;
using LinqToRest.Client.Materialization;
using LinqToRest.Client.Translation;
using LinqToRest.Modeling;
using LinqToRest.Protocol;

namespace LinqToRest.Client;

/// <summary>Builds LinqToRest queries and runs them over HTTP QUERY.</summary>
internal sealed class RestQueryProvider(RestDataContext context) : IQueryProvider
{
    // Building RestQueryable<T> for a runtime T needs MakeGenericType, which Native AOT can't promise; the Queryable
    // operators always call the generic overload.
    public IQueryable CreateQuery(Expression expression) =>
        throw new NotSupportedException("LinqToRest builds queries through the generic CreateQuery<TElement>; use the Queryable operators.");

    // C# builds decimal comparisons and arithmetic in lambdas without naming the operator method, so
    // System.Linq.Expressions looks it up by reflection; keep decimal's operators for trimmed and Native AOT apps.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(decimal))]
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new RestQueryable<TElement>(this, expression);

    public object Execute(Expression expression) => throw Synchronous();

    public TResult Execute<TResult>(Expression expression) => throw Synchronous();

    public static NotSupportedException Synchronous() =>
        new("LinqToRest never runs queries synchronously. Use the async operators, such as ToListAsync, FirstAsync or CountAsync.");

    /// <summary>The page for a query: a cursor page after <c>After</c>, else offset when the set allows it.</summary>
    public static PageRequest Page(TranslatedQuery query, int take)
    {
        var modes = query.Type.Pagination.Modes;
        if (query.After is not null)
        {
            return Cursor(query, query.After, take);
        }

        if (modes.HasFlag(PaginationModes.Offset))
        {
            return new PageRequest { Mode = PageMode.Offset, Skip = query.Skip, Take = take };
        }

        return query.Skip is null
            ? Cursor(query, null, take)
            : throw new NotSupportedException($"The set '{query.Type.SetName}' only allows cursor pagination. Use After instead of Skip.");
    }

    public static PageRequest Cursor(TranslatedQuery query, string? after, int take)
    {
        if (!query.Type.Pagination.Modes.HasFlag(PaginationModes.Cursor))
        {
            throw new NotSupportedException($"The set '{query.Type.SetName}' doesn't allow cursor pagination.");
        }

        if (query.Skip is not null)
        {
            throw new NotSupportedException("Skip can't be combined with cursor pagination.");
        }

        return new PageRequest { Mode = PageMode.Cursor, After = after, Take = take };
    }

    public async Task<QueryResponse<T>> SendAsync<T>(TranslatedQuery query, QueryRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Query, new Uri(query.Type.SetName, UriKind.Relative));
        message.Content = RestDataContext.JsonContent(QueryJson.Write(request));
        using var response = await context.Options.HttpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await Problems.QueryFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        var rows = root.GetProperty("items");
        List<T> items;
        if (!query.HasSelect && typeof(T) == query.Type.ClrType)
        {
            // Full rows: read through the bound delegates, then tracked unless AsNoTracking.
            items = new List<T>(rows.GetArrayLength());
            var index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                var read = RowReader.Read(query.Type, row, $"/items/{index++}");
                items.Add((T)(query.NoTracking ? read : context.Tracker.TrackQueried(read, query.Type)));
            }
        }
        else if (query.Selector is null)
        {
            // Count and Any ask for empty shapes: only how many items came back matters.
            items = [.. rows.EnumerateArray().Select(row => (T)(object)row.Clone())];
        }
        else
        {
            // Projections: read by following the Select lambda; never tracked.
            var read = ProjectionReader.Create(query.Type, query.Selector!);
            items = new List<T>(rows.GetArrayLength());
            var index = 0;
            foreach (var row in rows.EnumerateArray())
            {
                items.Add((T)read(row, $"/items/{index++}")!);
            }
        }

        return new QueryResponse<T>(
            items,
            root.TryGetProperty("nextCursor", out var next) ? next.GetString() : null,
            root.TryGetProperty("totalCount", out var total) ? total.GetInt64() : null);
    }
}

internal sealed record QueryResponse<T>(List<T> Items, string? NextCursor, long? TotalCount);

using System.Linq.Expressions;
using System.Text.Json;
using LinqToRest.Client.Translation;
using LinqToRest.Modeling;
using LinqToRest.Protocol;

namespace LinqToRest.Client;

/// <summary>Builds LinqToRest queries and runs them over HTTP QUERY.</summary>
internal sealed class RestQueryProvider(RestDataContext context) : IQueryProvider
{
    public IQueryable CreateQuery(Expression expression)
    {
        var elementType = expression.Type.GetInterfaces().Append(expression.Type)
            .First(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
            .GetGenericArguments()[0];
        return (IQueryable)Activator.CreateInstance(typeof(RestQueryable<>).MakeGenericType(elementType), this, expression)!;
    }

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
        var items = root.GetProperty("items").Deserialize<List<T>>(context.Options.SerializerOptions)!;
        if (query is { HasSelect: false, NoTracking: false } && typeof(T) == query.Type.ClrType)
        {
            for (var index = 0; index < items.Count; index++)
            {
                items[index] = (T)context.Tracker.TrackQueried(items[index]!, query.Type);
            }
        }

        return new QueryResponse<T>(
            items,
            root.TryGetProperty("nextCursor", out var next) ? next.GetString() : null,
            root.TryGetProperty("totalCount", out var total) ? total.GetInt64() : null);
    }
}

internal sealed record QueryResponse<T>(List<T> Items, string? NextCursor, long? TotalCount);

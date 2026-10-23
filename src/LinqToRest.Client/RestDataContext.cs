using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LinqToRest.Client.Materialization;
using LinqToRest.Client.Tracking;
using LinqToRest.Modeling;
using LinqToRest.Protocol;

namespace LinqToRest.Client;

/// <summary>
/// A unit of work over a LinqToRest API: query its sets with LINQ, change the tracked entities, and save everything
/// atomically with <see cref="SaveChangesAsync"/>.
/// </summary>
public abstract class RestDataContext
{
    private readonly Dictionary<Type, object> _sets = [];

    /// <summary>Creates the context.</summary>
    /// <param name="options">The model and HTTP client.</param>
    protected RestDataContext(RestDataContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Provider = new RestQueryProvider(this);
    }

    internal RestDataContextOptions Options { get; }

    internal RestQueryProvider Provider { get; }

    internal ChangeTracker Tracker { get; } = new();

    /// <summary>Gets the set of an entity type.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The set.</returns>
    public RestSet<TEntity> Set<TEntity>()
        where TEntity : class
    {
        if (!_sets.TryGetValue(typeof(TEntity), out var set))
        {
            var type = Options.Model.FindSet(typeof(TEntity)) ?? throw new InvalidOperationException($"{typeof(TEntity).Name} isn't the class of a set in the model.");
            set = new RestSet<TEntity>(this, type, Provider);
            _sets.Add(typeof(TEntity), set);
        }

        return (RestSet<TEntity>)set;
    }

    /// <summary>Gets the tracking information of an entity.</summary>
    /// <param name="entity">The entity.</param>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <returns>The entry.</returns>
    public EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new EntityEntry<TEntity>(Tracker, Set<TEntity>().Type, entity);
    }

    /// <summary>
    /// Sends every insert, update and delete as one atomic batch. On success the tracked entities get their generated
    /// keys, new concurrency tokens and computed members; on failure nothing is written and the tracked state stays
    /// as it was.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of operations sent.</returns>
    /// <exception cref="RestConcurrencyException">A row changed since it was read.</exception>
    /// <exception cref="RestSaveException">The server rejected the batch.</exception>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        Tracker.DetectChanges();
        foreach (var modified in Tracker.Entries.Where(entry => entry.State == EntityState.Modified))
        {
            ChangeTracker.RequireWrite(modified.Type, WriteOperations.Update, "updates");
        }

        var ordered = Order([.. Tracker.Entries.Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)]);
        if (ordered.Count == 0)
        {
            return 0;
        }

        // The model's built-in rules, before anything is sent; the server checks them (and its own) again.
        if (LocalValidation.Check(ordered) is { Count: > 0 } errors)
        {
            throw new RestValidationException((int)HttpStatusCode.UnprocessableEntity, "/operations", errors, isLocal: true, isTruncated: false);
        }

        var ids = new Dictionary<Entry, string>();
        foreach (var entry in ordered)
        {
            ids.Add(entry, $"o{ids.Count}");
        }

        var batch = new BatchRequest { Version = QueryJson.Version, Operations = [.. ordered.Select(entry => Operation(entry, ids))] };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("$batch", UriKind.Relative));
        request.Content = JsonContent(BatchJson.Write(batch));
        using var response = await Options.HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var byId = ids.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        if (!response.IsSuccessStatusCode)
        {
            throw await Problems.SaveFailureAsync(response, ordered, byId, cancellationToken).ConfigureAwait(false);
        }

        var result = BatchJson.ReadResponse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        foreach (var item in result.Results)
        {
            var entry = byId[item.Id];
            if (entry.State == EntityState.Deleted)
            {
                Tracker.Detach(entry);
                continue;
            }

            if (item.Value is { } value)
            {
                // New keys, tokens and computed values, straight onto the tracked row.
                RowReader.ReadInto(entry.Type, value, entry.Entity, new RowPointer($"/results/{item.Id}/value"), navigations: false);
            }

            Tracker.AcceptSaved(entry);
        }

        return ordered.Count;
    }

    /// <summary>
    /// Checks an entity against the model's built-in rules (every writable member), without sending anything. The
    /// server may have more rules of its own.
    /// </summary>
    /// <param name="entity">An entity of a set of this context.</param>
    /// <returns>The broken rules; empty when the entity passes.</returns>
    public IReadOnlyList<RestValidationError> Validate(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var type = Options.Model.FindSet(entity.GetType()) ?? throw new InvalidOperationException($"{entity.GetType().Name} isn't the class of a set in the model.");
        return [.. LocalValidation.Check(entity, type.Properties.Where(property => property.IsWritable))];
    }

    internal static ByteArrayContent JsonContent(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>Inserts first (each after the inserts it references), then updates, then deletes.</summary>
    private static List<Entry> Order(List<Entry> entries)
    {
        var ordered = new List<Entry>();
        var visiting = new HashSet<Entry>();
        void Visit(Entry entry)
        {
            if (ordered.Contains(entry))
            {
                return;
            }

            if (!visiting.Add(entry))
            {
                throw new InvalidOperationException($"New {entry.Type} entities reference each other in a cycle; save them in separate batches.");
            }

            foreach (var target in entry.References.Values.Where(target => target.State == EntityState.Added))
            {
                Visit(target);
            }

            ordered.Add(entry);
        }

        foreach (var entry in entries.Where(entry => entry.State == EntityState.Added))
        {
            Visit(entry);
        }

        ordered.AddRange(entries.Where(entry => entry.State == EntityState.Modified));
        ordered.AddRange(entries.Where(entry => entry.State == EntityState.Deleted));
        return ordered;
    }

    private static BatchOperation Operation(Entry entry, Dictionary<Entry, string> ids)
    {
        var type = entry.Type;
        var refs = entry.References.Count == 0 ? null : entry.References.ToDictionary(pair => pair.Key.WireName, pair => ids[pair.Value], StringComparer.Ordinal);
        IEnumerable<ClientProperty> written = entry.State == EntityState.Added ? type.Properties.Where(property => property.IsWritable) : entry.Modified;
        var values = entry.State == EntityState.Deleted
            ? null
            : written.Where(property => !entry.References.ContainsKey(property))
                .ToDictionary(property => property.WireName, property => JsonValues.ToElement(property.Get(entry.Entity)), StringComparer.Ordinal);
        var key = entry.State == EntityState.Added
            ? null
            : type.Key.Select((property, index) => (property.WireName, Value: entry.Key!.Value.Values[index]))
                .ToDictionary(pair => pair.WireName, pair => JsonValues.ToElement(pair.Value), StringComparer.Ordinal);
        JsonElement? token = entry.State != EntityState.Added && type.ConcurrencyToken is { } tokenProperty ? JsonValues.ToElement(entry.Original(tokenProperty)) : null;
        return new BatchOperation
        {
            Id = ids[entry],
            Op = entry.State switch
            {
                EntityState.Added => BatchOperationKind.Insert,
                EntityState.Modified => BatchOperationKind.Update,
                _ => BatchOperationKind.Delete,
            },
            Set = type.SetName,
            Key = key,
            Values = values,
            Refs = refs,
            Token = token,
        };
    }
}

using System.Net.Http.Headers;
using System.Text.Json;
using RestToLinq.Client.Tracking;
using RestToLinq.Modeling;
using RestToLinq.Protocol;

namespace RestToLinq.Client;

/// <summary>
/// A unit of work over a RestToLinq API: query its sets with LINQ, change the tracked DTOs, and save everything
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

    /// <summary>Gets the shared DTO model.</summary>
    public DtoModel Model => Options.Model;

    internal RestDataContextOptions Options { get; }

    internal RestQueryProvider Provider { get; }

    internal ChangeTracker Tracker { get; } = new();

    /// <summary>Gets the set of a DTO type.</summary>
    /// <typeparam name="TDto">The DTO type.</typeparam>
    /// <returns>The set.</returns>
    public DtoSet<TDto> Set<TDto>()
        where TDto : class, IDto
    {
        if (!_sets.TryGetValue(typeof(TDto), out var set))
        {
            var type = Model.FindType(typeof(TDto)) ?? throw new InvalidOperationException($"{typeof(TDto).Name} isn't part of the DTO model.");
            set = new DtoSet<TDto>(this, type, Provider);
            _sets.Add(typeof(TDto), set);
        }

        return (DtoSet<TDto>)set;
    }

    /// <summary>Gets the tracking information of a DTO.</summary>
    /// <param name="dto">The DTO.</param>
    /// <typeparam name="TDto">The DTO type.</typeparam>
    /// <returns>The entry.</returns>
    public EntityEntry<TDto> Entry<TDto>(TDto dto)
        where TDto : class, IDto
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new EntityEntry<TDto>(Tracker, Set<TDto>().Type, dto);
    }

    /// <summary>
    /// Sends every insert, update and delete as one atomic batch. On success the tracked DTOs get their generated
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
        var ordered = Order([.. Tracker.Entries.Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)]);
        if (ordered.Count == 0)
        {
            return 0;
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
            throw await Problems.SaveFailureAsync(response, byId, cancellationToken).ConfigureAwait(false);
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
                var saved = value.Deserialize(entry.Type.ClrType, Options.SerializerOptions)!;
                foreach (var property in entry.Type.Properties)
                {
                    property.PropertyInfo.SetValue(entry.Entity, property.PropertyInfo.GetValue(saved));
                }
            }

            Tracker.AcceptSaved(entry);
        }

        return ordered.Count;
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
                throw new InvalidOperationException($"New {entry.Type} DTOs reference each other in a cycle; save them in separate batches.");
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
        IEnumerable<DtoProperty> written = entry.State == EntityState.Added ? type.Properties.Where(property => property.IsWritable) : entry.Modified;
        var values = entry.State == EntityState.Deleted
            ? null
            : written.Where(property => !entry.References.ContainsKey(property))
                .ToDictionary(property => property.WireName, property => JsonValues.ToElement(property.PropertyInfo.GetValue(entry.Entity)), StringComparer.Ordinal);
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

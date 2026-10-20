using System.Buffers;
using System.Linq.Expressions;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Protocol;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Query;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.Batch;

/// <summary>
/// Applies a batch in one transaction: validates every operation against the model, writes only through members
/// exposed with <c>Property(...)</c>, saves once, re-reads changed rows through the set (one query per set), and rolls
/// everything back on any failure.
/// </summary>
internal sealed class BatchExecutor(ServerModel server, DbContext db, RequestPolicies policies)
{
    private readonly Dictionary<string, Applied> _applied = new(StringComparer.Ordinal);

    public async Task<BatchResponse> ExecuteAsync(BatchRequest batch, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            for (var index = 0; index < batch.Operations.Count; index++)
            {
                var operation = batch.Operations[index];
                try
                {
                    await ApplyAsync(operation, $"/operations/{index}", cancellationToken).ConfigureAwait(false);
                }
                catch (ProtocolException exception)
                {
                    throw RequestException.From(exception, [operation.Id]);
                }
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                var entities = exception.Entries.Select(entry => entry.Entity).ToHashSet();
                throw Concurrency([.. _applied.Values.Where(applied => entities.Contains(applied.Entity)).Select(applied => applied.Operation.Id)]);
            }
            catch (DbUpdateException exception) when (exception.InnerException is { } rejected && ProviderRejections.IsRejection(rejected))
            {
                // The provider refused a value; report it like other provider rejections (400), not as a conflict.
                ExceptionDispatchInfo.Throw(rejected);
            }
            catch (DbUpdateException)
            {
                throw new RequestException(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "");
            }

            var values = await ReadBackAsync(batch, cancellationToken).ConfigureAwait(false);
            var results = batch.Operations.Select(operation => new BatchResult { Id = operation.Id, Value = values.TryGetValue(operation.Id, out var value) ? value : null }).ToList();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BatchResponse { Results = results };
        }
    }

    private async Task ApplyAsync(BatchOperation operation, string location, CancellationToken cancellationToken)
    {
        var mapping = server.FindSet(operation.Set) ?? throw Failure(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"{location}/set", operation);
        var (kind, write) = operation.Op switch
        {
            BatchOperationKind.Insert => (SetOperation.Insert, WriteOperations.Insert),
            BatchOperationKind.Update => (SetOperation.Update, WriteOperations.Update),
            _ => (SetOperation.Delete, WriteOperations.Delete),
        };

        // The set's own rules come first: a read-only set never reaches its policy or the database.
        if (!mapping.Model.Allows(write))
        {
            throw Failure(StatusCodes.Status403Forbidden, ErrorCodes.OperationNotAllowed, location, operation);
        }

        if (!await policies.CanAsync(mapping, kind).ConfigureAwait(false))
        {
            throw Failure(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, location, operation);
        }

        object entity;
        if (kind == SetOperation.Insert)
        {
            if (operation.Key is not null || operation.Token is not null)
            {
                throw Invalid(ErrorCodes.InvalidBatch, location, operation);
            }

            entity = Activator.CreateInstance(mapping.EntityType)
                ?? throw new InvalidOperationException($"{mapping.EntityType.Name} needs a public parameterless constructor for inserts.");
            ApplyValues(mapping, entity, operation, location);
            await ApplyReferencesAsync(mapping, entity, operation, location, cancellationToken).ConfigureAwait(false);
            policies.Get(mapping)?.OnInsert(entity, policies.Context);
            db.Add(entity);
        }
        else
        {
            if (kind == SetOperation.Delete && (operation.Values is not null || operation.Refs is not null))
            {
                throw Invalid(ErrorCodes.InvalidBatch, location, operation);
            }

            entity = await LoadAsync(mapping, operation, location, cancellationToken).ConfigureAwait(false);
            CheckToken(mapping, entity, operation, location);
            if (kind == SetOperation.Delete)
            {
                db.Remove(entity);
            }
            else
            {
                ApplyValues(mapping, entity, operation, location);
                await ApplyReferencesAsync(mapping, entity, operation, location, cancellationToken).ConfigureAwait(false);
                policies.Get(mapping)?.OnUpdate(entity, policies.Context);
            }
        }

        _applied.Add(operation.Id, new Applied(operation, mapping, entity));
    }

    private async Task<object> LoadAsync(SetMapping mapping, BatchOperation operation, string location, CancellationToken cancellationToken)
    {
        var key = mapping.Key;
        if (operation.Key is null || operation.Key.Count != key.Length)
        {
            throw Invalid(ErrorCodes.InvalidBatch, $"{location}/key", operation);
        }

        var values = key.Select(property => operation.Key.TryGetValue(property.Name, out var value)
                ? JsonValues.Read(value, property.ClrType, $"{location}/key")
                : throw Invalid(ErrorCodes.InvalidBatch, $"{location}/key", operation))
            .ToList();
        var entity = await mapping.FindAsync(db, policies.Filter(mapping), values, cancellationToken).ConfigureAwait(false)
            ?? throw Failure(StatusCodes.Status404NotFound, ErrorCodes.NotFound, location, operation);

        // A FromSource with AsNoTracking returns detached rows; attaching makes the changes count.
        if (db.Entry(entity).State == EntityState.Detached)
        {
            db.Attach(entity);
        }

        return entity;
    }

    private void CheckToken(SetMapping mapping, object entity, BatchOperation operation, string location)
    {
        if (mapping.ConcurrencyToken is not { } token)
        {
            if (operation.Token is not null)
            {
                throw Invalid(ErrorCodes.InvalidBatch, $"{location}/token", operation);
            }

            return;
        }

        if (operation.Token is not { } element)
        {
            throw Invalid(ErrorCodes.InvalidBatch, $"{location}/token", operation);
        }

        var expected = JsonValues.Read(element, token.ClrType, $"{location}/token");
        var property = db.Entry(entity).Property(token.EntityProperty!.Name);
        if (!TokensEqual(property.CurrentValue, expected))
        {
            throw Concurrency([operation.Id]);
        }

        // Covers a change between this read and the save.
        property.OriginalValue = expected;
    }

    private static void ApplyValues(SetMapping mapping, object entity, BatchOperation operation, string location)
    {
        foreach (var (name, element) in operation.Values ?? new Dictionary<string, JsonElement>())
        {
            var property = mapping.FindProperty(name) ?? throw Invalid(ErrorCodes.UnknownMember, $"{location}/values", operation);
            if (!property.IsWritable)
            {
                throw Invalid(ErrorCodes.NotWritable, $"{location}/values", operation);
            }

            var value = JsonValues.Read(element, property.ClrType, $"{location}/values");
            if (value is null && !property.IsNullable)
            {
                throw Invalid(ErrorCodes.TypeMismatch, $"{location}/values", operation);
            }

            property.EntityProperty!.SetValue(entity, value);
        }
    }

    private async Task ApplyReferencesAsync(SetMapping mapping, object entity, BatchOperation operation, string location, CancellationToken cancellationToken)
    {
        foreach (var (name, target) in operation.Refs ?? new Dictionary<string, string>())
        {
            var navigation = mapping.Navigations.FirstOrDefault(navigation => navigation.ForeignKey?.Name == name);
            if (navigation is null || operation.Values?.ContainsKey(name) == true
                || !_applied.TryGetValue(target, out var referenced) || referenced.Operation.Op != BatchOperationKind.Insert || referenced.Mapping != navigation.Target)
            {
                throw Invalid(ErrorCodes.InvalidBatch, $"{location}/refs", operation);
            }

            navigation.EntityProperty.SetValue(entity, referenced.Entity);
        }

        // A foreign key written by value must point at a row the caller can see.
        foreach (var navigation in mapping.Navigations)
        {
            if (navigation.ForeignKey is not { } foreignKey || operation.Values?.TryGetValue(foreignKey.Name, out var element) != true
                || JsonValues.Read(element, foreignKey.ClrType, $"{location}/values") is not { } value)
            {
                continue;
            }

            var target = navigation.Target;
            if (await target.FindAsync(db, policies.Filter(target), [value], cancellationToken).ConfigureAwait(false) is null)
            {
                throw Failure(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"{location}/values", operation);
            }
        }
    }

    /// <summary>Reads every inserted or updated row back through the default projection, by operation id.</summary>
    private async Task<Dictionary<string, JsonElement>> ReadBackAsync(BatchRequest batch, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var written = batch.Operations.Where(operation => operation.Op != BatchOperationKind.Delete).Select(operation => _applied[operation.Id]);
        foreach (var set in written.GroupBy(applied => applied.Mapping))
        {
            var mapping = set.Key;
            var keys = set.Select(applied => (IReadOnlyList<object?>)[.. mapping.Key.Select(property => property.EntityProperty!.GetValue(applied.Entity))]).ToList();
            var root = Expression.Parameter(mapping.EntityType, "e");
            var row = new Row(root);
            var shape = new QueryBinder(server, policies).DefaultShape(new EntityBound(root, mapping, null, isNullable: false), row, null);
            var keyIndexes = mapping.Key.Select(property => row.Add(ExpressionHelpers.Inline(property.Binding, root))).ToList();
            var plan = new QueryPlan
            {
                PolicyFilter = policies.Filter(mapping),
                Filter = mapping.KeyPredicate(keys),
                Sorts = [],
                Projection = row.ToLambda(),
                Shape = shape,
                Mode = PageMode.Offset,
                Take = keys.Count,
            };

            var result = await mapping.ExecuteAsync(db, plan, cancellationToken).ConfigureAwait(false);
            foreach (var (applied, key) in set.Zip(keys))
            {
                // Rows the policy filter hides after the write come back without a value.
                if (result.Rows.FirstOrDefault(candidate => keyIndexes.Select(index => candidate[index]).SequenceEqual(key)) is { } match)
                {
                    values[applied.Operation.Id] = Serialize(shape, match);
                }
            }
        }

        return values;
    }

    private static JsonElement Serialize(Shape shape, object?[] row)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            shape.Write(writer, row);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static bool TokensEqual(object? current, object? expected) =>
        current is byte[] left && expected is byte[] right ? left.AsSpan().SequenceEqual(right) : Equals(current, expected);

    private static RequestException Invalid(string code, string location, BatchOperation operation) =>
        Failure(StatusCodes.Status400BadRequest, code, location, operation);

    private static RequestException Failure(int status, string code, string location, BatchOperation operation) =>
        new(status, code, location, operationIds: [operation.Id]);

    private static RequestException Concurrency(IReadOnlyList<string> operationIds) =>
        new(StatusCodes.Status412PreconditionFailed, ErrorCodes.ConcurrencyConflict, "", operationIds: operationIds);

    private sealed record Applied(BatchOperation Operation, SetMapping Mapping, object Entity);
}

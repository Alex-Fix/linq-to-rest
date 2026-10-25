using System.Buffers;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using LinqToRest.Modeling;
using LinqToRest.Protocol;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.Internal;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Query;
using LinqToRest.Server.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.Batch;

/// <summary>
/// Applies a batch in one transaction: validates every operation against the model, writes only through members
/// exposed with <c>Property(...)</c> or <c>Value(...)</c> (whole values, built from their exposed members), saves once,
/// re-reads changed rows through the set (one query per set), and rolls everything back on any failure.
/// </summary>
internal sealed class BatchExecutor(ServerModel server, DbContext db, RequestPolicies policies)
{
    private readonly Dictionary<string, Applied> _applied = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, object> _validators = [];

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

            // Every rule of every operation, before anything is written: a form gets all its errors at once.
            var issues = await ValidateAsync(batch, cancellationToken).ConfigureAwait(false);
            if (issues.Count > 0)
            {
                throw RequestException.Validation(issues);
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
        if (operation.Op is BatchOperationKind.Link or BatchOperationKind.Unlink)
        {
            await ApplyLinkAsync(mapping, operation, location, cancellationToken).ConfigureAwait(false);
            return;
        }

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

            entity = await LoadAsync(mapping, operation.Key, $"{location}/key", location, operation, cancellationToken).ConfigureAwait(false);
            CheckToken(mapping, entity, operation.Token, $"{location}/token", operation);
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

    /// <summary>
    /// Adds an item to, or removes it from, a collection the set allows links on. Parent and item are loaded through
    /// their policies (or come from earlier inserts); only the affected membership is loaded; EF Core then sets the
    /// item's foreign key or adds or removes the join row. Unlinking a required one-to-many item deletes it.
    /// </summary>
    private async Task ApplyLinkAsync(SetMapping mapping, BatchOperation operation, string location, CancellationToken cancellationToken)
    {
        var navigation = mapping.Navigations.FirstOrDefault(navigation => navigation.Name == operation.Navigation && navigation.IsCollection)
            ?? throw Invalid(ErrorCodes.InvalidBatch, $"{location}/navigation", operation);
        if (navigation.Links is not { } links)
        {
            throw Failure(StatusCodes.Status403Forbidden, ErrorCodes.OperationNotAllowed, $"{location}/navigation", operation);
        }

        var unlink = operation.Op == BatchOperationKind.Unlink;
        var target = navigation.Target;

        // A one-to-many link rewrites the item's row (or deletes it); a many-to-many one only a join row.
        SetOperation? itemOperation = links == RestLinkKind.ManyToMany ? null
            : unlink && navigation.OnUnlink == RestUnlinkBehavior.Delete ? SetOperation.Delete : SetOperation.Update;
        if (!await policies.CanAsync(mapping, SetOperation.Update).ConfigureAwait(false)
            || (itemOperation is { } operationOnItem && !await policies.CanAsync(target, operationOnItem).ConfigureAwait(false)))
        {
            throw Failure(StatusCodes.Status403Forbidden, ErrorCodes.Forbidden, location, operation);
        }

        var parent = operation.Ref is { } parentRef
            ? Inserted(parentRef, mapping, $"{location}/ref", operation)
            : await LoadAsync(mapping, operation.Key, $"{location}/key", $"{location}/key", operation, cancellationToken).ConfigureAwait(false);
        var item = operation.Item!;
        object entity;
        if (item.Ref is { } itemRef)
        {
            entity = Inserted(itemRef, target, $"{location}/item/ref", operation);
            if (item.Token is not null)
            {
                throw Invalid(ErrorCodes.InvalidBatch, $"{location}/item/token", operation);
            }
        }
        else
        {
            entity = await LoadAsync(target, item.Key, $"{location}/item/key", $"{location}/item/key", operation, cancellationToken).ConfigureAwait(false);
            if (links == RestLinkKind.OneToMany)
            {
                CheckToken(target, entity, item.Token, $"{location}/item/token", operation);
            }
            else if (item.Token is not null)
            {
                throw Invalid(ErrorCodes.InvalidBatch, $"{location}/item/token", operation);
            }
        }

        // Only rows already in the database can already be members; load just that membership.
        var collection = db.Entry(parent).Navigation(navigation.EfNavigation.Name);
        if (db.Entry(parent).State != EntityState.Added && db.Entry(entity).State != EntityState.Added)
        {
            var itemKey = target.Key.Select(property => property.EntityProperty!.GetValue(entity)).ToList();
            await target.LoadAsync(((Microsoft.EntityFrameworkCore.ChangeTracking.CollectionEntry)collection).Query(), target.KeyPredicate([itemKey]), cancellationToken).ConfigureAwait(false);
        }

        var accessor = navigation.EfNavigation.GetCollectionAccessor()!;
        var member = accessor.Contains(parent, entity);
        if (!unlink)
        {
            if (!member)
            {
                accessor.Add(parent, entity, forMaterialization: false);
            }
        }
        else if (!member)
        {
            // Never confirms membership the caller can't change: not a member reads like a missing row.
            throw Failure(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"{location}/item", operation);
        }
        else
        {
            if (navigation.OnUnlink == RestUnlinkBehavior.Delete)
            {
                db.Remove(entity);
            }

            accessor.Remove(parent, entity);
        }

        // A one-to-many item's row changes like an update's, so its set's update hook runs too.
        if (itemOperation == SetOperation.Update)
        {
            policies.Get(target)?.OnUpdate(entity, policies.Context);
        }

        _applied.Add(operation.Id, new Applied(operation, target, entity));
    }

    /// <summary>The row an earlier insert of <paramref name="mapping"/> added.</summary>
    private object Inserted(string id, SetMapping mapping, string location, BatchOperation operation) =>
        _applied.TryGetValue(id, out var referenced) && referenced.Operation.Op == BatchOperationKind.Insert && referenced.Mapping == mapping
            ? referenced.Entity
            : throw Invalid(ErrorCodes.InvalidBatch, location, operation);

    private async Task<List<ValidationIssue>> ValidateAsync(BatchRequest batch, CancellationToken cancellationToken)
    {
        var max = server.Limits.MaxValidationErrors;
        var issues = new List<ValidationIssue>();
        for (var index = 0; index < batch.Operations.Count && issues.Count <= max; index++)
        {
            var operation = batch.Operations[index];
            if (operation.Op is BatchOperationKind.Insert or BatchOperationKind.Update)
            {
                var (_, mapping, entity) = _applied[operation.Id];
                var scope = new ValidationScope(operation, $"/operations/{index}", mapping, policies.Context, issues);
                CheckRules(scope, operation, entity);
                foreach (var type in mapping.Validators)
                {
                    await mapping.ValidateAsync(Validator(type), entity, scope, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return issues.Count <= max ? issues : [.. issues.Take(max), new ValidationIssue { Code = ErrorCodes.TooManyErrors }];
    }

    /// <summary>
    /// Checks the built-in rules, then the <c>Must</c> rules, of one inserted or updated row. An insert checks every
    /// member; an update only the members it sends (and every row rule), so older data a newer rule rejects doesn't
    /// block an edit. One error per member: a member that broke a rule isn't checked again.
    /// </summary>
    private static void CheckRules(ValidationScope scope, BatchOperation operation, object entity)
    {
        var isInsert = operation.Op == BatchOperationKind.Insert;
        var failed = new HashSet<SetProperty>();
        foreach (var property in scope.Mapping.Properties)
        {
            if (property.ValueType is { } valueType)
            {
                if (property.IsWritable && (isInsert || scope.IsSent(property)) && CheckValue(scope, property, valueType, entity))
                {
                    failed.Add(property);
                }

                continue;
            }

            // A foreign key set through refs gets its value when the row it references is saved.
            if (property.Rules is { } rules && (isInsert || scope.IsSent(property)) && operation.Refs?.ContainsKey(property.Name) != true
                && rules.Check(property.EntityProperty!.GetValue(entity)) is { } code)
            {
                failed.Add(property);
                scope.Add(property, code);
            }
        }

        foreach (var rule in scope.Mapping.Rules)
        {
            var skipped = rule.Member is { } member && (failed.Contains(member) || (rule.OnlyWhenSent && !isInsert && !scope.IsSent(member)));
            if (!skipped && !rule.IsValid(entity))
            {
                if (rule.Member is not null)
                {
                    failed.Add(rule.Member);
                }

                scope.Add(rule.Member, rule.Code);
            }
        }
    }

    /// <summary>
    /// Checks a written value: <c>required</c> when a non-nullable value is missing (an insert that didn't send one and
    /// whose entity has none), and the built-in rules of its members, nested ones too. Returns whether it failed.
    /// </summary>
    private static bool CheckValue(ValidationScope scope, SetProperty property, ServerValueType valueType, object entity)
    {
        var value = property.EntityProperty!.GetValue(entity);
        if (value is null)
        {
            if (property.IsNullable)
            {
                return false;
            }

            scope.Add(property.Name, ErrorCodes.Required);
            return true;
        }

        var count = scope.Count;
        CheckMembers(scope, property.Name, valueType, value);
        return scope.Count > count;
    }

    private static void CheckMembers(ValidationScope scope, string path, ServerValueType valueType, object value)
    {
        foreach (var member in valueType.Properties)
        {
            var memberValue = member.EntityProperty!.GetValue(value);
            if (member.ValueType is { } nested)
            {
                if (memberValue is not null)
                {
                    CheckMembers(scope, $"{path}/{member.Name}", nested, memberValue);
                }
            }
            else if (member.Rules?.Check(memberValue) is { } code)
            {
                scope.Add($"{path}/{member.Name}", code);
            }
        }
    }

    // One instance per validator type and request, from the request's services.
    private object Validator(Type type)
    {
        if (!_validators.TryGetValue(type, out var validator))
        {
            validator = ActivatorUtilities.CreateInstance(policies.Context.Services, type);
            _validators.Add(type, validator);
        }

        return validator;
    }

    /// <summary>Loads the row with the given key through the set's policy filter: hidden or missing is a 404 at <paramref name="notFound"/>.</summary>
    private async Task<object> LoadAsync(
        SetMapping mapping, IReadOnlyDictionary<string, JsonElement>? keyValues, string keyLocation, string notFound, BatchOperation operation, CancellationToken cancellationToken)
    {
        var key = mapping.Key;
        if (keyValues is null || keyValues.Count != key.Length)
        {
            throw Invalid(ErrorCodes.InvalidBatch, keyLocation, operation);
        }

        var values = key.Select(property => keyValues.TryGetValue(property.Name, out var value)
                ? JsonValues.Read(value, property.ClrType, keyLocation)
                : throw Invalid(ErrorCodes.InvalidBatch, keyLocation, operation))
            .ToList();
        var entity = await mapping.FindAsync(db, policies.Filter(mapping), values, cancellationToken).ConfigureAwait(false)
            ?? throw Failure(StatusCodes.Status404NotFound, ErrorCodes.NotFound, notFound, operation);

        // A FromSource with AsNoTracking returns detached rows; attaching makes the changes count.
        if (db.Entry(entity).State == EntityState.Detached)
        {
            db.Attach(entity);
        }

        return entity;
    }

    private void CheckToken(SetMapping mapping, object entity, JsonElement? sent, string location, BatchOperation operation)
    {
        if (mapping.ConcurrencyToken is not { } token)
        {
            if (sent is not null)
            {
                throw Invalid(ErrorCodes.InvalidBatch, location, operation);
            }

            return;
        }

        if (sent is not { } element)
        {
            throw Invalid(ErrorCodes.InvalidBatch, location, operation);
        }

        var expected = JsonValues.Read(element, token.ClrType, location);
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
            // An unknown name came from the client and isn't echoed; known members are named by the model.
            var property = mapping.FindProperty(name) ?? throw Invalid(ErrorCodes.UnknownMember, $"{location}/values", operation);
            var pointer = $"{location}/values/{property.Name}";
            if (!property.IsWritable)
            {
                throw Invalid(ErrorCodes.NotWritable, pointer, operation);
            }

            var value = property.ValueType is { } valueType
                ? ReadValue(valueType, element, pointer, operation)
                : JsonValues.Read(element, property.ClrType, pointer);
            if (value is null && !property.IsNullable)
            {
                throw Invalid(ErrorCodes.TypeMismatch, pointer, operation);
            }

            property.EntityProperty!.SetValue(entity, value);
        }
    }

    /// <summary>
    /// Builds a new value from a JSON object holding every exposed member (nested values too), with the construction
    /// plan resolved at startup. The value replaces the old one as a whole.
    /// </summary>
    private static object? ReadValue(ServerValueType valueType, JsonElement element, string pointer, BatchOperation operation)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(ErrorCodes.TypeMismatch, pointer, operation);
        }

        var values = new Dictionary<SetProperty, object?>();
        foreach (var member in element.EnumerateObject())
        {
            // An unknown name came from the client and isn't echoed; the pointer names the value.
            var property = valueType.FindProperty(member.Name) ?? throw Invalid(ErrorCodes.UnknownMember, pointer, operation);
            var memberPointer = $"{pointer}/{property.Name}";
            var value = property.ValueType is { } nested
                ? ReadValue(nested, member.Value, memberPointer, operation)
                : JsonValues.Read(member.Value, property.ClrType, memberPointer);
            if (value is null && !property.IsNullable)
            {
                throw Invalid(ErrorCodes.TypeMismatch, memberPointer, operation);
            }

            if (!values.TryAdd(property, value))
            {
                throw Invalid(ErrorCodes.InvalidBatch, pointer, operation);
            }
        }

        if (Array.Find(valueType.Properties, property => !values.ContainsKey(property)) is { } missing)
        {
            throw Invalid(ErrorCodes.InvalidBatch, $"{pointer}/{missing.Name}", operation);
        }

        var construction = valueType.Construction!;
        try
        {
            var created = construction.Constructor.Invoke([.. construction.Arguments.Select(property => values[property])]);
            foreach (var setter in construction.Setters)
            {
                setter.EntityProperty!.SetValue(created, values[setter]);
            }

            return created;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException)
        {
            // The value type's own checks refused the values (a guard in its constructor or setter).
            throw Invalid(ErrorCodes.TypeMismatch, pointer, operation);
        }
    }

    private async Task ApplyReferencesAsync(SetMapping mapping, object entity, BatchOperation operation, string location, CancellationToken cancellationToken)
    {
        var refs = operation.Refs ?? new Dictionary<string, string>();
        var used = 0;
        foreach (var navigation in mapping.Navigations.Where(navigation => navigation.ForeignKey.Count > 0))
        {
            // A ref names every member of one foreign key, all pointing at the same earlier insert of the target set.
            var targets = navigation.ForeignKey.Select(property => refs.GetValueOrDefault(property.Name)).ToList();
            if (targets.TrueForAll(target => target is null))
            {
                continue;
            }

            used += targets.Count;
            if (targets.Distinct().Count() != 1 || navigation.ForeignKey.Any(property => operation.Values?.ContainsKey(property.Name) == true)
                || !_applied.TryGetValue(targets[0]!, out var referenced) || referenced.Operation.Op != BatchOperationKind.Insert || referenced.Mapping != navigation.Target)
            {
                throw Invalid(ErrorCodes.InvalidBatch, $"{location}/refs", operation);
            }

            navigation.EntityProperty.SetValue(entity, referenced.Entity);
        }

        if (used != refs.Count)
        {
            throw Invalid(ErrorCodes.InvalidBatch, $"{location}/refs", operation);
        }

        // A foreign key written by value must point at a row the caller can see.
        foreach (var navigation in mapping.Navigations)
        {
            if (navigation.ForeignKey.FirstOrDefault(property => operation.Values?.ContainsKey(property.Name) == true) is not { } sent)
            {
                continue;
            }

            // The whole key as the row now holds it: a composite key may be written in part.
            var key = navigation.ForeignKey.Select(property => property.EntityProperty!.GetValue(entity)).ToList();
            if (key.Contains(null))
            {
                continue;
            }

            var target = navigation.Target;
            if (await target.FindAsync(db, policies.Filter(target), key, cancellationToken).ConfigureAwait(false) is null)
            {
                throw Failure(StatusCodes.Status404NotFound, ErrorCodes.NotFound, $"{location}/values/{sent.Name}", operation);
            }
        }
    }

    /// <summary>Reads every inserted or updated row back through the default projection, by operation id.</summary>
    private async Task<Dictionary<string, JsonElement>> ReadBackAsync(BatchRequest batch, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var written = batch.Operations.Where(operation => operation.Op is BatchOperationKind.Insert or BatchOperationKind.Update).Select(operation => _applied[operation.Id]);
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

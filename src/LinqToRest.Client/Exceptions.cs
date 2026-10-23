using LinqToRest.Protocol;

namespace LinqToRest.Client;

/// <summary>The server rejected a query. <see cref="Code"/> is one of LinqToRest's stable error codes.</summary>
public sealed class RestQueryException : Exception
{
    internal RestQueryException(int status, string code, string? location, string? limit)
        : base(limit is null ? $"The query was rejected: {code} at '{location}'." : $"The query was rejected: {code} ({limit}) at '{location}'.")
    {
        Status = status;
        Code = code;
        Location = location;
        Limit = limit;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public int Status { get; }

    /// <summary>Gets the error code, such as <c>unknown_member</c> or <c>limit_exceeded</c>.</summary>
    public string Code { get; }

    /// <summary>Gets the JSON pointer of the rejected node in the request.</summary>
    public string? Location { get; }

    /// <summary>Gets the name of the exceeded limit, if any.</summary>
    public string? Limit { get; }
}

/// <summary>The server rejected a save. Nothing was written and the tracked state is unchanged.</summary>
public class RestSaveException : Exception
{
    internal RestSaveException(int status, string code, string? location, IReadOnlyList<object> entities, string? member = null)
        : base($"The changes were rejected: {code} at '{location}'.")
    {
        Status = status;
        Code = code;
        Location = location;
        Entities = entities;
        Member = member;
    }

    /// <summary>
    /// Gets the CLR name of the member the error is about (for example <c>Price</c> for <c>not_writable</c>), or
    /// <see langword="null"/> when it's about a whole row or the batch.
    /// </summary>
    public string? Member { get; }

    /// <summary>Gets the HTTP status code.</summary>
    public int Status { get; }

    /// <summary>Gets the error code, such as <c>not_writable</c> or <c>not_found</c>.</summary>
    public string Code { get; }

    /// <summary>Gets the JSON pointer of the rejected part of the batch.</summary>
    public string? Location { get; }

    /// <summary>Gets the tracked entities whose operations were rejected.</summary>
    public IReadOnlyList<object> Entities { get; }
}

/// <summary>Rows changed since they were read. Reload them and retry.</summary>
public sealed class RestConcurrencyException : RestSaveException
{
    internal RestConcurrencyException(int status, string code, string? location, IReadOnlyList<object> entities)
        : base(status, code, location, entities)
    {
    }
}

/// <summary>
/// Values break validation rules (code <c>validation_failed</c>): found by the client before sending
/// (<see cref="IsLocal"/>), or by the server (422). Nothing was written and the tracked state is unchanged.
/// </summary>
public sealed class RestValidationException : RestSaveException
{
    internal RestValidationException(int status, string? location, IReadOnlyList<RestValidationError> errors, bool isLocal, bool isTruncated)
        : base(status, ErrorCodes.ValidationFailed, location, [.. errors.Select(error => error.Entity).Distinct()])
    {
        Errors = errors;
        IsLocal = isLocal;
        IsTruncated = isTruncated;
    }

    /// <summary>Gets every broken rule, in batch order.</summary>
    public IReadOnlyList<RestValidationError> Errors { get; }

    /// <summary>Gets whether the client found the errors before sending (the built-in rules only).</summary>
    public bool IsLocal { get; }

    /// <summary>Gets whether the server found more errors than it lists.</summary>
    public bool IsTruncated { get; }

    /// <summary>Returns the error codes of one entity by CLR member name; errors about the whole row are under <c>""</c>.</summary>
    /// <param name="entity">The tracked entity.</param>
    /// <returns>The codes by member.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ErrorsFor(object entity) =>
        Errors.Where(error => ReferenceEquals(error.Entity, entity))
            .GroupBy(error => error.Member ?? "", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, IReadOnlyList<string> (group) => [.. group.Select(error => error.Code)], StringComparer.Ordinal);
}

/// <summary>One broken rule: the entity, the CLR name of its member (or null for the whole row), and the code.</summary>
/// <param name="Entity">The tracked entity.</param>
/// <param name="Member">The CLR member name, such as <c>Price</c>, or <see langword="null"/> for the whole row.</param>
/// <param name="Code">The code: a built-in one such as <c>required</c>, or one of the server's own.</param>
public sealed record RestValidationError(object Entity, string? Member, string Code);


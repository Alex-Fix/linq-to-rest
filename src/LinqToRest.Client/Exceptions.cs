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
    internal RestSaveException(int status, string code, string? location, IReadOnlyList<object> entities)
        : base($"The changes were rejected: {code} at '{location}'.")
    {
        Status = status;
        Code = code;
        Location = location;
        Entities = entities;
    }

    /// <summary>Gets the HTTP status code.</summary>
    public int Status { get; }

    /// <summary>Gets the error code, such as <c>not_writable</c> or <c>not_found</c>.</summary>
    public string Code { get; }

    /// <summary>Gets the JSON pointer of the rejected part of the batch.</summary>
    public string? Location { get; }

    /// <summary>Gets the tracked DTOs whose operations were rejected.</summary>
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

namespace LinqToRest.Protocol;

/// <summary>
/// A request that violates the LinqToRest protocol. Carries a stable <see cref="Code"/> and a JSON pointer to the
/// offending node, never the offending value.
/// </summary>
public sealed class ProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">One of <see cref="ErrorCodes"/>.</param>
    /// <param name="location">The JSON pointer of the offending node (empty for the whole body).</param>
    /// <param name="limit">The name of the exceeded limit, for <see cref="ErrorCodes.LimitExceeded"/>.</param>
    public ProtocolException(string code, string location, string? limit = null)
        : base(limit is null ? $"{code} at '{location}'" : $"{code} ({limit}) at '{location}'")
    {
        Code = code;
        Location = location;
        Limit = limit;
    }

    /// <summary>Gets the error code, one of <see cref="ErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>Gets the JSON pointer of the offending node.</summary>
    public string Location { get; }

    /// <summary>Gets the name of the exceeded limit, if any.</summary>
    public string? Limit { get; }
}

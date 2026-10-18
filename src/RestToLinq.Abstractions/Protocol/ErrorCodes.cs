namespace RestToLinq.Protocol;

/// <summary>Stable error codes returned in the <c>code</c> member of RestToLinq problem responses.</summary>
public static class ErrorCodes
{
    /// <summary>The body isn't valid JSON (including duplicate keys).</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>A node or key isn't part of the protocol.</summary>
    public const string UnknownNode = "unknown_node";

    /// <summary>An operator got the wrong number of arguments.</summary>
    public const string BadArity = "bad_arity";

    /// <summary>A member path doesn't resolve to a DTO member or lambda variable.</summary>
    public const string UnknownMember = "unknown_member";

    /// <summary>The member can't be filtered.</summary>
    public const string NotFilterable = "not_filterable";

    /// <summary>The member can't be sorted.</summary>
    public const string NotSortable = "not_sortable";

    /// <summary>The member can't be written.</summary>
    public const string NotWritable = "not_writable";

    /// <summary>A value or operand has the wrong type.</summary>
    public const string TypeMismatch = "type_mismatch";

    /// <summary>The cursor is malformed or doesn't match the query's sort.</summary>
    public const string InvalidCursor = "invalid_cursor";

    /// <summary>Cursor pagination was requested with a nullable sort member.</summary>
    public const string CursorNullableSort = "cursor_nullable_sort";

    /// <summary>A server limit was exceeded; the <c>limit</c> member names it.</summary>
    public const string LimitExceeded = "limit_exceeded";

    /// <summary>The protocol version isn't supported.</summary>
    public const string UnsupportedVersion = "unsupported_version";

    /// <summary>The query is structurally invalid (for example skip combined with a cursor).</summary>
    public const string InvalidQuery = "invalid_query";

    /// <summary>The batch is structurally invalid (for example a reference to an unknown operation).</summary>
    public const string InvalidBatch = "invalid_batch";

    /// <summary>The set or row doesn't exist or isn't visible.</summary>
    public const string NotFound = "not_found";

    /// <summary>A policy denied the operation.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>A row changed since it was read; the <c>operationIds</c> member lists the affected operations.</summary>
    public const string ConcurrencyConflict = "concurrency_conflict";

    /// <summary>The request isn't <c>application/json</c>.</summary>
    public const string UnsupportedMediaType = "unsupported_media_type";
}

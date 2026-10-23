namespace LinqToRest.Protocol;

/// <summary>Size limits for query and batch bodies, enforced while reading them.</summary>
public sealed class QueryLimits
{
    /// <summary>Gets or sets the largest query body in bytes. Default: 64 KB.</summary>
    public int MaxBodyBytes { get; set; } = 64 * 1024;

    /// <summary>Gets or sets the deepest node nesting. Default: 16.</summary>
    public int MaxDepth { get; set; } = 16;

    /// <summary>Gets or sets the most nodes in one query. Default: 500.</summary>
    public int MaxNodes { get; set; } = 500;

    /// <summary>Gets or sets the deepest nesting of collection lambdas (any, all, sum, map…). Default: 3.</summary>
    public int MaxLambdaNesting { get; set; } = 3;

    /// <summary>Gets or sets the most values in one <c>in</c> list. Default: 500.</summary>
    public int MaxInListLength { get; set; } = 500;

    /// <summary>Gets or sets the most navigations one member path may traverse. Default: 3.</summary>
    public int MaxPathDepth { get; set; } = 3;

    /// <summary>Gets or sets the longest cursor in characters. Default: 2048.</summary>
    public int MaxCursorLength { get; set; } = 2048;

    /// <summary>Gets or sets the largest batch body in bytes. Default: 1 MB.</summary>
    public int MaxBatchBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>Gets or sets the most operations in one batch. Default: 100.</summary>
    public int MaxBatchOperations { get; set; } = 100;

    /// <summary>Gets or sets the most keys of a grouped query. Default: 4.</summary>
    public int MaxGroupKeys { get; set; } = 4;

    /// <summary>
    /// Gets or sets the most groups a grouped query without <c>take</c> returns; more is <c>limit_exceeded</c>
    /// (<c>groups</c>). With <c>take</c>, the set's page limits apply. Default: 1000.
    /// </summary>
    public int MaxGroups { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the most validation errors one <c>validation_failed</c> problem lists; a final
    /// <c>too_many_errors</c> entry says more were found. Default: 100.
    /// </summary>
    public int MaxValidationErrors { get; set; } = 100;
}

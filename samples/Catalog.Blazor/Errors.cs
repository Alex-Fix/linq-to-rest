using LinqToRest.Client;

namespace Catalog.Blazor;

public static class Errors
{
    /// <summary>A message for a failed call: the problem's stable code and where in the request it points, never raw values.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        RestConcurrencyException => "Someone else changed this since you loaded it.",
        RestSaveException save => $"{Message(save.Code)} ({save.Code} at {save.Location ?? "/"})",
        RestQueryException query => $"{Message(query.Code)} ({query.Code} at {query.Location ?? "/"})",
        _ => exception.Message,
    };

    private static string Message(string code) => code switch
    {
        "forbidden" => "You aren't allowed to do that. Sign in as an editor.",
        "operation_not_allowed" => "This kind of change isn't allowed on this set.",
        "conflict" => "The database refused the change (for example a duplicate SKU, or a category that still has products).",
        "not_found" => "It no longer exists, or you can't see it.",
        _ => "The server rejected the request.",
    };
}

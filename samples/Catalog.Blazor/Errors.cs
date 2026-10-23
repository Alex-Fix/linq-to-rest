using LinqToRest.Client;
using LinqToRest.Modeling;

namespace Catalog.Blazor;

public static class Errors
{
    /// <summary>A message for a failed call: the problem's stable code and where in the request it points, never raw values.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        RestConcurrencyException => "Someone else changed this since you loaded it.",
        RestValidationException validation =>
            $"{(validation.IsLocal ? "Some values aren't valid" : "The server rejected some values")} ({string.Join(" ", validation.Errors.Select(error => $"{error.Member ?? "row"}: {FieldMessage(error.Code, null)}"))})",
        RestSaveException save => $"{Message(save.Code)} ({save.Code} at {save.Location ?? "/"})",
        RestQueryException query => $"{Message(query.Code)} ({query.Code} at {query.Location ?? "/"})",
        _ => exception.Message,
    };

    private static string Message(string code) => code switch
    {
        "forbidden" => "You aren't allowed to do that. Sign in as an editor.",
        "operation_not_allowed" => "This kind of change isn't allowed on this set.",
        "conflict" => "The database refused the change (for example a category that still has products).",
        "not_found" => "It no longer exists, or you can't see it.",
        _ => "The server rejected the request.",
    };

    /// <summary>
    /// A field's message for a validation code: built-in codes read the member's rules (so limits come from the
    /// server's model), the catalog API's own codes are spelled out, anything else shows the code.
    /// </summary>
    public static string FieldMessage(string code, RestPropertyRules? rules) => code switch
    {
        "required" => "Required.",
        "too_short" => rules?.MinLength is { } min ? $"At least {min} characters." : "Too short.",
        "too_long" => rules?.MaxLength is { } max ? $"At most {max} characters." : "Too long.",
        "out_of_range" => (rules?.Min, rules?.Max) switch
        {
            ({ } min, { } max) => $"Between {min} and {max}.",
            ({ } min, null) => $"At least {min}.",
            (null, { } max) => $"At most {max}.",
            _ => "Out of range.",
        },
        "invalid_format" => "Doesn't have the expected format.",
        "duplicate" => "Already taken by another product.",
        "too_precise" => "Use whole cents (at most two decimals).",
        _ => code,
    };
}

using System.Text.RegularExpressions;
using LinqToRest.Protocol;

namespace LinqToRest.Server.Sets;

/// <summary>Custom validation codes: <c>snake_case</c> and distinct from the built-in ones, so clients can rely on them.</summary>
internal static partial class ValidationCodes
{
    private static readonly HashSet<string> BuiltIn = new(StringComparer.Ordinal)
    {
        ErrorCodes.Required, ErrorCodes.TooShort, ErrorCodes.TooLong, ErrorCodes.OutOfRange, ErrorCodes.InvalidFormat,
        ErrorCodes.TooManyErrors, ErrorCodes.ValidationFailed,
    };

    public static bool IsValid(string? code) => code is not null && CodePattern().IsMatch(code) && !BuiltIn.Contains(code);

    public static string Check(string code, string? parameterName = "code") =>
        IsValid(code)
            ? code
            : throw new ArgumentException("A validation code is snake_case (a lowercase letter, then lowercase letters, digits or '_'), at most 64 characters, and not a built-in code.", parameterName);

    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

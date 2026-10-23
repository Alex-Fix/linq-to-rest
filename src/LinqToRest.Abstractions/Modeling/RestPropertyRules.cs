using System.Collections;
using System.Text.RegularExpressions;
using LinqToRest.Protocol;

namespace LinqToRest.Modeling;

/// <summary>
/// The built-in validation rules of a writable property, exported in the model document. Servers enforce them on
/// inserts and updates; clients check them before sending. <see cref="Check"/> is the one definition both use.
/// </summary>
public sealed class RestPropertyRules
{
    /// <summary>The longest pattern a model may declare.</summary>
    public const int MaxPatternLength = 500;

    /// <summary>How long a pattern may take on one value before the value counts as not matching.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    // JavaScript's \s: ECMAScript whitespace and line terminators.
    private const string JavaScriptSpaces = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";

    private Regex? _regex;

    /// <summary>Gets whether the value must be present: not null, and for strings and collections not empty or blank.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Gets the minimum length of a string, or item count of a collection.</summary>
    public int? MinLength { get; init; }

    /// <summary>Gets the maximum length of a string, or item count of a collection.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Gets the inclusive lower bound of a number, date or time, as a value of the property's type.</summary>
    public object? Min { get; init; }

    /// <summary>Gets the inclusive upper bound of a number, date or time, as a value of the property's type.</summary>
    public object? Max { get; init; }

    /// <summary>Gets the ECMAScript pattern a string must match (anchor it with <c>^…$</c> to match the whole value).</summary>
    public string? Pattern { get; init; }

    /// <summary>Gets whether no rule is set.</summary>
    public bool IsEmpty => !IsRequired && MinLength is null && MaxLength is null && Min is null && Max is null && Pattern is null;

    /// <summary>Checks a value against the rules, in the order required, length, range, pattern.</summary>
    /// <param name="value">The value: a scalar of the property's type, a string, a collection or null.</param>
    /// <returns>The error code of the first broken rule, such as <see cref="ErrorCodes.Required"/>, or <see langword="null"/>.</returns>
    public string? Check(object? value)
    {
        if (value is null)
        {
            return IsRequired ? ErrorCodes.Required : null;
        }

        var length = value switch
        {
            string text => text.Length,
            ICollection items => items.Count,
            _ => (int?)null,
        };
        if (IsRequired && (length == 0 || (value is string blank && string.IsNullOrWhiteSpace(blank))))
        {
            return ErrorCodes.Required;
        }

        if (length is { } count && (count < MinLength || count > MaxLength))
        {
            return count < MinLength ? ErrorCodes.TooShort : ErrorCodes.TooLong;
        }

        if ((Min is not null && Compare(value, Min) < 0) || (Max is not null && Compare(value, Max) > 0))
        {
            return ErrorCodes.OutOfRange;
        }

        if (Pattern is not null && value is string input)
        {
            try
            {
                if (!(_regex ??= CreateRegex(Pattern)).IsMatch(input))
                {
                    return ErrorCodes.InvalidFormat;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return ErrorCodes.InvalidFormat;
            }
        }

        return null;
    }

    /// <summary>Compiles a pattern the way <see cref="Check"/> runs it: as JavaScript would, with a match timeout.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>The regular expression.</returns>
    /// <exception cref="ArgumentException">The pattern isn't in the portable subset, or isn't valid.</exception>
    internal static Regex CreateRegex(string pattern) => new(ToDotNet(pattern), RegexOptions.ECMAScript, PatternTimeout);

    /// <summary>
    /// Checks that a pattern means the same in .NET and in JavaScript (no flags) and returns its .NET form. .NET's
    /// ECMAScript mode accepts constructs JavaScript lacks or reads differently, so those are refused, and the two that
    /// differ in meaning are rewritten: <c>$</c> (.NET also matches before a final newline) and <c>\s</c> (JavaScript
    /// includes Unicode spaces).
    /// </summary>
    private static string ToDotNet(string pattern)
    {
        var output = new System.Text.StringBuilder(pattern.Length + 16);
        var inClass = false;
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            var next = index + 1 < pattern.Length ? pattern[index + 1] : '\0';
            if (current == '\\')
            {
                switch (next)
                {
                    case '\0':
                        throw NotPortable("ends with a backslash");
                    case 'p' or 'P':
                        throw NotPortable("uses \\p, which JavaScript only supports with the u flag");
                    case 'A' or 'Z' or 'z' or 'G' when !inClass:
                        throw NotPortable($"uses \\{next}, which JavaScript doesn't have (use ^ and $)");
                    case 's':
                        output.Append(inClass ? JavaScriptSpaces : $"[{JavaScriptSpaces}]");
                        break;
                    case 'S':
                        output.Append(inClass ? throw NotPortable("uses \\S inside a character class") : $"[^{JavaScriptSpaces}]");
                        break;
                    default:
                        output.Append(current).Append(next);
                        break;
                }

                index++;
                continue;
            }

            if (inClass)
            {
                if (current == '-' && next == '[')
                {
                    throw NotPortable("uses character class subtraction");
                }

                inClass = current != ']';
                output.Append(current);
                continue;
            }

            switch (current)
            {
                case '[':
                    inClass = true;
                    output.Append(current);
                    if (next == '^')
                    {
                        output.Append(next);
                        index++;
                        next = index + 1 < pattern.Length ? pattern[index + 1] : '\0';
                    }

                    if (next == ']')
                    {
                        throw NotPortable("has an empty character class or a leading ']', which JavaScript reads differently");
                    }

                    break;
                case '(' when next == '?':
                    var rest = pattern.AsSpan(index + 2);
                    if (!(rest.StartsWith(":") || rest.StartsWith("=") || rest.StartsWith("!") || rest.StartsWith("<=") || rest.StartsWith("<!")
                        || (rest.Length > 1 && rest[0] == '<' && char.IsAsciiLetter(rest[1]))))
                    {
                        throw NotPortable("uses a (? construct JavaScript doesn't have, such as inline options or atomic groups");
                    }

                    output.Append(current);
                    break;
                case '$':
                    output.Append("\\z");
                    break;
                default:
                    output.Append(current);
                    break;
            }
        }

        return output.ToString();
    }

    private static ArgumentException NotPortable(string reason) => new($"The pattern {reason}.");

    // Bounds have the property's type (checked when the model is built), so they compare directly.
    private static int Compare(object value, object bound) =>
        value is IComparable comparable && value.GetType() == bound.GetType() ? comparable.CompareTo(bound) : 0;
}

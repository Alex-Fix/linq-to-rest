using LinqToRest.Modeling;
using LinqToRest.Protocol;

namespace LinqToRest.Abstractions.UnitTests.Modeling;

/// <summary>The one definition of the built-in rules, shared by the server and the .NET client.</summary>
public sealed class RestPropertyRulesTests
{
    [Fact]
    public void Required_rejects_null_empty_and_blank_values()
    {
        var rules = new RestPropertyRules { IsRequired = true };

        Assert.Equal(ErrorCodes.Required, rules.Check(null));
        Assert.Equal(ErrorCodes.Required, rules.Check(""));
        Assert.Equal(ErrorCodes.Required, rules.Check(" \t"));
        Assert.Equal(ErrorCodes.Required, rules.Check(new List<string>()));
        Assert.Null(rules.Check("a"));
        Assert.Null(rules.Check(0));
        Assert.Null(rules.Check(new List<int> { 1 }));
    }

    [Fact]
    public void Optional_values_pass_when_null()
    {
        var rules = new RestPropertyRules { MinLength = 2, Min = 1, Pattern = "^a$" };

        Assert.Null(rules.Check(null));
    }

    [Theory]
    [InlineData("a", "too_short")]
    [InlineData("ab", null)]
    [InlineData("abcd", null)]
    [InlineData("abcde", "too_long")]
    public void Lengths_are_inclusive(string value, string? expected) =>
        Assert.Equal(expected, new RestPropertyRules { MinLength = 2, MaxLength = 4 }.Check(value));

    [Fact]
    public void Collection_lengths_count_items()
    {
        var rules = new RestPropertyRules { MaxLength = 2 };

        Assert.Null(rules.Check(new List<string> { "a", "b" }));
        Assert.Equal(ErrorCodes.TooLong, rules.Check(new List<string> { "a", "b", "c" }));
    }

    [Fact]
    public void Ranges_are_inclusive_and_either_bound_is_optional()
    {
        var both = new RestPropertyRules { Min = 0.01m, Max = 100m };
        var from = new RestPropertyRules { Min = new DateOnly(2026, 1, 1) };

        Assert.Equal(ErrorCodes.OutOfRange, both.Check(0m));
        Assert.Null(both.Check(0.01m));
        Assert.Null(both.Check(100m));
        Assert.Equal(ErrorCodes.OutOfRange, both.Check(100.01m));
        Assert.Equal(ErrorCodes.OutOfRange, from.Check(new DateOnly(2025, 12, 31)));
        Assert.Null(from.Check(new DateOnly(2099, 1, 1)));
    }

    [Fact]
    public void The_first_broken_rule_wins()
    {
        var rules = new RestPropertyRules { IsRequired = true, MaxLength = 3, Pattern = "^[a-z]+$" };

        Assert.Equal(ErrorCodes.Required, rules.Check(""));
        Assert.Equal(ErrorCodes.TooLong, rules.Check("ABCD"));
        Assert.Equal(ErrorCodes.InvalidFormat, rules.Check("AB"));
    }

    [Theory]
    [InlineData("^[A-Z0-9-]{3,20}$", "AB-12", true)]
    [InlineData("^[A-Z0-9-]{3,20}$", "AB-12\n", false)]   // .NET's $ would match before the newline; JavaScript's doesn't
    [InlineData("^[A-Z0-9-]{3,20}$", "ab-12", false)]
    [InlineData("^\\S+$", "a b", false)]             // JavaScript's \s includes no-break space
    [InlineData("^[\\s\\d]+$", "1 2", true)]
    [InlineData("^\\d+$", "١٢", false)]                    // ECMAScript \d is ASCII only
    [InlineData("^(?<a>x)\\k<a>$", "xx", true)]
    [InlineData("^a(?=b)", "ab", true)]
    public void Patterns_match_as_JavaScript_does(string pattern, string value, bool matches) =>
        Assert.Equal(matches ? null : ErrorCodes.InvalidFormat, new RestPropertyRules { Pattern = pattern }.Check(value));

    [Theory]
    [InlineData("\\p{L}")]
    [InlineData("\\Aa")]
    [InlineData("a\\z")]
    [InlineData("(?i)a")]
    [InlineData("(?>a)")]
    [InlineData("(?#comment)a")]
    [InlineData("[a-z-[aeiou]]")]
    [InlineData("[]a]")]
    [InlineData("[\\S]")]
    [InlineData("a\\")]
    [InlineData("a++")]
    public void Non_portable_patterns_are_refused(string pattern) =>
        Assert.ThrowsAny<ArgumentException>(() => RestPropertyRules.CreateRegex(pattern));
}

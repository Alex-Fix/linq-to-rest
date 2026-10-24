using System.Text;
using System.Text.Json;
using LinqToRest.Protocol;

namespace LinqToRest.Abstractions.UnitTests.Protocol;

public sealed class QueryJsonTests
{
    private const string DesignExample = """
        {"v":1,
         "filter":{"and":[
           {"gt":[{"member":"price"},10]},
           {"any":{"source":{"member":"tags"},"var":"t","body":{"eq":[{"var":"t"},"sale"]}}},
           {"in":[{"member":"category/name"},["Books","Games"]]}]},
         "orderBy":[{"member":"price","desc":true},{"member":"name"}],
         "select":{"new":{
           "id":{"member":"id"},
           "total":{"sum":{"source":{"member":"lines"},"var":"l","body":{"mul":[{"member":"l/qty"},{"member":"l/price"}]}}},
           "label":{"concat":[{"member":"name"}," (",{"member":"category/name"},")"]},
           "lines":{"map":{"source":{"member":"lines"},"var":"l","where":{"gt":[{"member":"l/qty"},0]},"body":{"new":{"sku":{"member":"l/sku"}}}}}}},
         "include":["category"],
         "page":{"mode":"cursor","after":"eyJ2Ijox","take":20},
         "count":true}
        """;

    [Fact]
    public void Reads_the_design_example()
    {
        var query = Read(DesignExample);

        var and = Assert.IsType<CallNode>(query.Filter);
        Assert.Equal("and", and.Op);
        Assert.Equal(3, and.Arguments.Count);
        var any = Assert.IsType<LambdaNode>(and.Arguments[1]);
        Assert.Equal(("any", "t"), (any.Op, any.Variable));
        Assert.Equal("/filter/and/1", any.Location);
        var list = Assert.IsType<ListNode>(Assert.IsType<CallNode>(and.Arguments[2]).Arguments[1]);
        Assert.Equal(["Books", "Games"], list.Items.Select(item => item.Value.GetString()));
        Assert.Equal(["price", "name"], query.OrderBy.Select(item => item.Member.Path));
        Assert.True(query.OrderBy[0].Descending);
        var shape = Assert.IsType<NewNode>(query.Select);
        Assert.Equal(["id", "total", "label", "lines"], shape.Fields.Select(field => field.Key));
        var map = Assert.IsType<LambdaNode>(shape.Fields[3].Value);
        Assert.IsType<NewNode>(map.Body);
        Assert.NotNull(map.Where);
        Assert.Equal("category", Assert.Single(query.Include).Path);
        Assert.Equal((PageMode.Cursor, 20, "eyJ2Ijox"), (query.Page!.Mode, query.Page.Take, query.Page.After));
        Assert.True(query.Count);
    }

    [Fact]
    public void Write_then_read_round_trips()
    {
        var first = QueryJson.Write(Read(DesignExample));
        var second = QueryJson.Write(QueryJson.Read(first, new QueryLimits()));

        Assert.Equal(Encoding.UTF8.GetString(first), Encoding.UTF8.GetString(second));
        using var expected = JsonDocument.Parse(DesignExample);
        using var actual = JsonDocument.Parse(first);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    private const string GroupedExample = """
        {"v":1,
         "filter":{"eq":[{"member":"status"},"Active"]},
         "groupBy":["brandId","brand/name"],
         "orderBy":[{"member":"count","desc":true}],
         "select":{"new":{
           "brandId":{"key":0},
           "name":{"key":1},
           "count":{"count":{"source":{"group":{}},"var":"v0","body":true}},
           "inStock":{"count":{"source":{"group":{}},"var":"v1","where":{"gt":[{"member":"v1/stock"},0]},"body":true}}}},
         "having":{"gt":[{"member":"count"},1]},
         "page":{"mode":"offset","take":20}}
        """;

    [Fact]
    public void Reads_and_writes_grouped_queries()
    {
        var query = Read(GroupedExample);
        var written = QueryJson.Write(query);

        Assert.Equal(["brandId", "brand/name"], query.GroupBy!.Select(key => key.Path));
        Assert.Equal("/having", query.Having!.Location);
        var fields = ((NewNode)query.Select!).Fields;
        Assert.Equal((1, "/select/new/name"), (((KeyNode)fields[1].Value).Index, fields[1].Value.Location));
        Assert.IsType<GroupNode>(((LambdaNode)fields[2].Value).Source);
        using var expected = JsonDocument.Parse(GroupedExample);
        using var actual = JsonDocument.Parse(written);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
        Assert.Equal(Encoding.UTF8.GetString(written), Encoding.UTF8.GetString(QueryJson.Write(QueryJson.Read(written, new QueryLimits()))));
    }

    [Fact]
    public void An_empty_group_by_is_one_group_and_a_missing_one_is_no_grouping()
    {
        Assert.Empty(Read("""{"v":1,"groupBy":[],"select":{"new":{"n":{"count":{"source":{"group":{}},"var":"v0","body":true}}}}}""").GroupBy!);
        Assert.Null(Read("""{"v":1}""").GroupBy);
        Assert.DoesNotContain("groupBy", Encoding.UTF8.GetString(QueryJson.Write(new QueryRequest())), StringComparison.Ordinal);
    }

    [Fact]
    public void Group_key_limit_is_enforced_at_the_boundary()
    {
        var limits = new QueryLimits { MaxGroupKeys = 2 };

        Read("""{"v":1,"groupBy":["a","b"]}""", limits);
        AssertLimit("""{"v":1,"groupBy":["a","b","c"]}""", limits, "groupKeys");
    }

    [Fact]
    public void Writes_literals_from_clr_values()
    {
        var query = new QueryRequest
        {
            Filter = new CallNode("eq", [new MemberNode("day"), LiteralNode.From(new DateOnly(2026, 10, 18))]),
            Page = new PageRequest { Mode = PageMode.Offset, Skip = 40, Take = 20 },
        };

        Assert.Equal(
            """{"v":1,"filter":{"eq":[{"member":"day"},"2026-10-18"]},"page":{"mode":"offset","skip":40,"take":20}}""",
            Encoding.UTF8.GetString(QueryJson.Write(query)));
    }

    [Theory]
    [InlineData("""{"filter":{"member":"a"}}""", ErrorCodes.UnsupportedVersion, "/v")]
    [InlineData("""{"v":2}""", ErrorCodes.UnsupportedVersion, "/v")]
    [InlineData("""{"v":1,"v":1}""", ErrorCodes.InvalidJson, "")]
    [InlineData("""{"v":1""", ErrorCodes.InvalidJson, "")]
    [InlineData("""[1]""", ErrorCodes.InvalidQuery, "")]
    [InlineData("""{"v":1,"extra":true}""", ErrorCodes.UnknownNode, "")]
    [InlineData("""{"v":1,"filter":{"fooBar":[1]}}""", ErrorCodes.UnknownNode, "/filter")]
    [InlineData("""{"v":1,"filter":{"eq":[1,2],"ne":[1,2]}}""", ErrorCodes.UnknownNode, "/filter")]
    [InlineData("""{"v":1,"filter":{}}""", ErrorCodes.UnknownNode, "/filter")]
    [InlineData("""{"v":1,"filter":[1]}""", ErrorCodes.UnknownNode, "/filter")]
    [InlineData("""{"v":1,"filter":{"eq":[1]}}""", ErrorCodes.BadArity, "/filter/eq")]
    [InlineData("""{"v":1,"filter":{"not":true}}""", ErrorCodes.BadArity, "/filter/not")]
    [InlineData("""{"v":1,"filter":{"all":{"source":{"member":"a"}}}}""", ErrorCodes.BadArity, "/filter/all")]
    [InlineData("""{"v":1,"filter":{"any":{"source":{"member":"a"},"body":true}}}""", ErrorCodes.BadArity, "/filter/any")]
    [InlineData("""{"v":1,"filter":{"any":{"source":{"member":"a"},"var":"1x"}}}""", ErrorCodes.UnknownMember, "/filter/any/var")]
    [InlineData("""{"v":1,"filter":{"member":"a//b"}}""", ErrorCodes.UnknownMember, "/filter/member")]
    [InlineData("""{"v":1,"filter":{"member":"a-b"}}""", ErrorCodes.UnknownMember, "/filter/member")]
    [InlineData("""{"v":1,"filter":{"in":[{"member":"a"},"x"]}}""", ErrorCodes.TypeMismatch, "/filter/in/1")]
    [InlineData("""{"v":1,"filter":{"in":[{"member":"a"},[{"member":"b"}]]}}""", ErrorCodes.TypeMismatch, "/filter/in/1/0")]
    [InlineData("""{"v":1,"filter":{"new":{"a":1}}}""", ErrorCodes.UnknownNode, "/filter")]
    [InlineData("""{"v":1,"filter":{"map":{"source":{"member":"a"},"var":"x","body":1}}}""", ErrorCodes.UnknownNode, "/filter")]
    [InlineData("""{"v":1,"select":{"new":{"a-b":1}}}""", ErrorCodes.UnknownNode, "/select/new")]
    [InlineData("""{"v":1,"orderBy":[{"member":"a","asc":true}]}""", ErrorCodes.UnknownNode, "/orderBy/0")]
    [InlineData("""{"v":1,"orderBy":[{"desc":true}]}""", ErrorCodes.InvalidQuery, "/orderBy/0")]
    [InlineData("""{"v":1,"page":{"take":10}}""", ErrorCodes.InvalidQuery, "/page")]
    [InlineData("""{"v":1,"page":{"mode":"cursor","skip":10}}""", ErrorCodes.InvalidQuery, "/page")]
    [InlineData("""{"v":1,"page":{"mode":"offset","after":"x"}}""", ErrorCodes.InvalidQuery, "/page")]
    [InlineData("""{"v":1,"page":{"mode":"offset","take":-1}}""", ErrorCodes.InvalidQuery, "/page/take")]
    [InlineData("""{"v":1,"page":{"mode":"pages"}}""", ErrorCodes.InvalidQuery, "/page/mode")]
    [InlineData("""{"v":1,"count":"yes"}""", ErrorCodes.InvalidQuery, "/count")]
    [InlineData("""{"v":1,"groupBy":"a"}""", ErrorCodes.InvalidQuery, "/groupBy")]
    [InlineData("""{"v":1,"groupBy":[{"member":"a"}]}""", ErrorCodes.UnknownMember, "/groupBy/0")]
    [InlineData("""{"v":1,"select":{"key":-1}}""", ErrorCodes.InvalidQuery, "/select/key")]
    [InlineData("""{"v":1,"select":{"key":"a"}}""", ErrorCodes.InvalidQuery, "/select/key")]
    [InlineData("""{"v":1,"select":{"count":{"source":{"group":{"a":1}},"var":"v","body":true}}}""", ErrorCodes.InvalidQuery, "/select/count/source/group")]
    public void Rejects_invalid_queries(string json, string code, string location)
    {
        var error = Assert.Throws<ProtocolException>(() => Read(json));

        Assert.Equal((code, location), (error.Code, error.Location));
    }

    [Fact]
    public void Node_limit_is_enforced_at_the_boundary()
    {
        var limits = new QueryLimits { MaxNodes = 3 };

        Read("""{"v":1,"filter":{"eq":[{"member":"a"},1]}}""", limits);
        AssertLimit("""{"v":1,"filter":{"and":[{"member":"a"},{"member":"b"}]},"orderBy":[{"member":"a"}]}""", limits, "nodes");
    }

    [Fact]
    public void Depth_limit_is_enforced_at_the_boundary()
    {
        var limits = new QueryLimits { MaxDepth = 3 };

        Read("""{"v":1,"filter":{"not":[{"not":[{"member":"a"}]}]}}""", limits);
        AssertLimit("""{"v":1,"filter":{"not":[{"not":[{"not":[{"member":"a"}]}]}]}}""", limits, "depth");
    }

    [Fact]
    public void Lambda_nesting_limit_is_enforced_at_the_boundary()
    {
        var limits = new QueryLimits { MaxLambdaNesting = 1 };
        const string one = """{"any":{"source":{"member":"a"},"var":"x","body":true}}""";
        const string two = """{"any":{"source":{"member":"a"},"var":"x","body":{"any":{"source":{"member":"x/b"}}}}}""";

        Read("""{"v":1,"filter":{"and":[""" + one + "," + one + "]}}", limits);
        AssertLimit("""{"v":1,"filter":""" + two + "}", limits, "lambdaNesting");
    }

    [Fact]
    public void In_list_limit_is_enforced_at_the_boundary()
    {
        var limits = new QueryLimits { MaxInListLength = 2 };

        Read("""{"v":1,"filter":{"in":[{"member":"a"},[1,2]]}}""", limits);
        AssertLimit("""{"v":1,"filter":{"in":[{"member":"a"},[1,2,3]]}}""", limits, "inListLength");
    }

    [Fact]
    public void Path_depth_limit_is_enforced_at_the_boundary()
    {
        var limits = new QueryLimits { MaxPathDepth = 1 };

        Read("""{"v":1,"filter":{"member":"x/nav/name"}}""", limits);
        AssertLimit("""{"v":1,"filter":{"member":"x/nav/nav/name"}}""", limits, "pathDepth");
    }

    [Fact]
    public void Body_size_limit_is_enforced_at_the_boundary()
    {
        const string json = """{"v":1}""";
        var limits = new QueryLimits { MaxBodyBytes = json.Length };

        Read(json, limits);
        AssertLimit("""{"v":1} """, limits, "bodySize");
    }

    [Fact]
    public void Cursor_length_limit_is_enforced_at_the_boundary()
    {
        var limits = new QueryLimits { MaxCursorLength = 4 };

        Read("""{"v":1,"page":{"mode":"cursor","after":"abcd"}}""", limits);
        AssertLimit("""{"v":1,"page":{"mode":"cursor","after":"abcde"}}""", limits, "cursorLength");
    }

    private static QueryRequest Read(string json, QueryLimits? limits = null) =>
        QueryJson.Read(Encoding.UTF8.GetBytes(json), limits ?? new QueryLimits());

    private static void AssertLimit(string json, QueryLimits limits, string limit)
    {
        var error = Assert.Throws<ProtocolException>(() => Read(json, limits));

        Assert.Equal((ErrorCodes.LimitExceeded, limit), (error.Code, error.Limit));
    }
}

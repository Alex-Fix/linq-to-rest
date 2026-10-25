using System.Text;
using System.Text.Json;
using LinqToRest.Protocol;

namespace LinqToRest.Abstractions.UnitTests.Protocol;

public sealed class BatchJsonTests
{
    private const string DesignExample = """
        {"v":1,"operations":[
          {"id":"o0","op":"insert","set":"categories","values":{"name":"Games"}},
          {"id":"o1","op":"insert","set":"products","values":{"name":"Chess","price":20},"refs":{"categoryId":"o0"}},
          {"id":"o2","op":"update","set":"products","key":{"id":7},"values":{"price":12},"token":"AAAAB9E="},
          {"id":"o3","op":"delete","set":"products","key":{"id":9},"token":"AAAAB+A="}]}
        """;

    [Fact]
    public void Reads_the_design_example()
    {
        var batch = Read(DesignExample);

        Assert.Equal(
            [BatchOperationKind.Insert, BatchOperationKind.Insert, BatchOperationKind.Update, BatchOperationKind.Delete],
            batch.Operations.Select(operation => operation.Op));
        Assert.Equal("o0", batch.Operations[1].Refs!["categoryId"]);
        Assert.Equal(7, batch.Operations[2].Key!["id"].GetInt32());
        Assert.Equal("AAAAB9E=", batch.Operations[2].Token!.Value.GetString());
        Assert.Null(batch.Operations[3].Values);
    }

    [Fact]
    public void Reads_links_and_unlinks()
    {
        const string json = """
            {"v":1,"operations":[
              {"id":"o0","op":"insert","set":"orderLines","values":{"sku":"C3"}},
              {"id":"o1","op":"link","set":"products","key":{"id":7},"navigation":"lines","item":{"ref":"o0"}},
              {"id":"o2","op":"unlink","set":"products","key":{"id":7},"navigation":"lines","item":{"key":{"id":12},"token":"AAAAB9E="}},
              {"id":"o3","op":"insert","set":"products","values":{"name":"Cube"}},
              {"id":"o4","op":"link","set":"products","ref":"o3","navigation":"labels","item":{"key":{"id":3}}}]}
            """;

        var batch = Read(json);

        Assert.Equal(
            [BatchOperationKind.Insert, BatchOperationKind.Link, BatchOperationKind.Unlink, BatchOperationKind.Insert, BatchOperationKind.Link],
            batch.Operations.Select(operation => operation.Op));
        Assert.Equal(("lines", "o0"), (batch.Operations[1].Navigation, batch.Operations[1].Item!.Ref));
        Assert.Equal((12, "AAAAB9E="), (batch.Operations[2].Item!.Key!["id"].GetInt32(), batch.Operations[2].Item!.Token!.Value.GetString()));
        Assert.Equal("o3", batch.Operations[4].Ref);
        using var expected = JsonDocument.Parse(json);
        using var written = JsonDocument.Parse(BatchJson.Write(batch));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, written.RootElement));
    }

    [Fact]
    public void Write_then_read_round_trips()
    {
        var written = BatchJson.Write(Read(DesignExample));

        using var expected = JsonDocument.Parse(DesignExample);
        using var actual = JsonDocument.Parse(written);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    [Fact]
    public void Response_round_trips()
    {
        var response = new BatchResponse
        {
            Results = [new BatchResult { Id = "o0", Value = JsonValues.ToElement(15) }, new BatchResult { Id = "o1" }],
        };

        var read = BatchJson.ReadResponse(BatchJson.WriteResponse(response));

        Assert.Equal("""{"results":[{"id":"o0","value":15},{"id":"o1"}]}""", Encoding.UTF8.GetString(BatchJson.WriteResponse(response)));
        Assert.Equal(["o0", "o1"], read.Results.Select(result => result.Id));
    }

    [Theory]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"upsert","set":"s"}]}""", ErrorCodes.InvalidJson, "")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"insert","set":"s","extra":1}]}""", ErrorCodes.InvalidJson, "")]
    [InlineData("""{"v":1,"operations":[{"op":"insert","set":"s"}]}""", ErrorCodes.InvalidJson, "")]
    [InlineData("""{"v":1,"v":1,"operations":[]}""", ErrorCodes.InvalidJson, "")]
    [InlineData("""{"v":1,"operations":[null]}""", ErrorCodes.InvalidBatch, "/operations/0")]
    [InlineData("""{"v":1,"operations":[{"id":null,"op":"insert","set":"s"}]}""", ErrorCodes.InvalidJson, "")]
    [InlineData("""{"v":2,"operations":[{"id":"a","op":"insert","set":"s"}]}""", ErrorCodes.UnsupportedVersion, "/v")]
    [InlineData("""{"v":1,"operations":[]}""", ErrorCodes.InvalidBatch, "/operations")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"insert","set":"s"},{"id":"a","op":"insert","set":"s"}]}""", ErrorCodes.InvalidBatch, "/operations/1/id")]
    [InlineData("""{"v":1,"operations":[{"id":"a b","op":"insert","set":"s"}]}""", ErrorCodes.InvalidBatch, "/operations/0/id")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"insert","set":"s","navigation":"lines"}]}""", ErrorCodes.InvalidBatch, "/operations/0/navigation")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"update","set":"s","key":{"id":1},"item":{"key":{"id":1}}}]}""", ErrorCodes.InvalidBatch, "/operations/0/item")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"delete","set":"s","key":{"id":1},"ref":"b"}]}""", ErrorCodes.InvalidBatch, "/operations/0/ref")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","key":{"id":1},"navigation":"l","item":{"key":{"id":2}},"values":{}}]}""", ErrorCodes.InvalidBatch, "/operations/0/values")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","key":{"id":1},"navigation":"l","item":{"key":{"id":2}},"refs":{}}]}""", ErrorCodes.InvalidBatch, "/operations/0/refs")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"unlink","set":"s","key":{"id":1},"navigation":"l","item":{"key":{"id":2}},"token":"x"}]}""", ErrorCodes.InvalidBatch, "/operations/0/token")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","key":{"id":1},"item":{"key":{"id":2}}}]}""", ErrorCodes.InvalidBatch, "/operations/0/navigation")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","key":{"id":1},"navigation":"a b","item":{"key":{"id":2}}}]}""", ErrorCodes.InvalidBatch, "/operations/0/navigation")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","navigation":"l","item":{"key":{"id":2}}}]}""", ErrorCodes.InvalidBatch, "/operations/0/key")]
    [InlineData("""{"v":1,"operations":[{"id":"o","op":"insert","set":"s","values":{}},{"id":"a","op":"link","set":"s","key":{"id":1},"ref":"o","navigation":"l","item":{"key":{"id":2}}}]}""", ErrorCodes.InvalidBatch, "/operations/1/key")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","key":{"id":1},"navigation":"l"}]}""", ErrorCodes.InvalidBatch, "/operations/0/item")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","key":{"id":1},"navigation":"l","item":{}}]}""", ErrorCodes.InvalidBatch, "/operations/0/item")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","ref":"b","navigation":"l","item":{"key":{"id":2}}},{"id":"b","op":"insert","set":"s","values":{}}]}""", ErrorCodes.InvalidBatch, "/operations/0/ref")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","key":{"id":1},"navigation":"l","item":{"ref":"a"}}]}""", ErrorCodes.InvalidBatch, "/operations/0/item/ref")]
    [InlineData("""{"v":1,"operations":[{"id":"a","op":"link","set":"s","key":{"id":1},"navigation":"l","item":{"key":{"id":2},"extra":1}}]}""", ErrorCodes.InvalidJson, "")]
    public void Rejects_invalid_batches(string json, string code, string location)
    {
        var error = Assert.Throws<ProtocolException>(() => Read(json));

        Assert.Equal((code, location), (error.Code, error.Location));
    }

    [Fact]
    public void Operation_limit_is_enforced_at_the_boundary()
    {
        var limits = new QueryLimits { MaxBatchOperations = 2 };
        const string two = """{"v":1,"operations":[{"id":"a","op":"delete","set":"s"},{"id":"b","op":"delete","set":"s"}]}""";
        const string three = """{"v":1,"operations":[{"id":"a","op":"delete","set":"s"},{"id":"b","op":"delete","set":"s"},{"id":"c","op":"delete","set":"s"}]}""";

        Read(two, limits);
        var error = Assert.Throws<ProtocolException>(() => Read(three, limits));

        Assert.Equal((ErrorCodes.LimitExceeded, "batchOperations"), (error.Code, error.Limit));
    }

    [Fact]
    public void Body_size_limit_is_enforced_at_the_boundary()
    {
        const string json = """{"v":1,"operations":[{"id":"a","op":"delete","set":"s"}]}""";
        var limits = new QueryLimits { MaxBatchBodyBytes = json.Length };

        Read(json, limits);
        var error = Assert.Throws<ProtocolException>(() => Read(json + " ", limits));

        Assert.Equal((ErrorCodes.LimitExceeded, "bodySize"), (error.Code, error.Limit));
    }

    private static BatchRequest Read(string json, QueryLimits? limits = null) =>
        BatchJson.Read(Encoding.UTF8.GetBytes(json), limits ?? new QueryLimits());

    [Fact]
    public void Validation_issues_round_trip_with_their_wire_names()
    {
        ValidationIssue[] issues =
        [
            new() { Operation = "o0", Location = "/operations/0/values/price", Member = "price", Code = ErrorCodes.OutOfRange },
            new() { Operation = "o1", Location = "/operations/1", Code = "sale_ends_before_release" },
            new() { Code = ErrorCodes.TooManyErrors },
        ];

        var errors = BatchJson.WriteIssues(issues);
        var read = BatchJson.ReadIssues(errors);

        Assert.Equal(
            """[{"operation":"o0","pointer":"/operations/0/values/price","member":"price","code":"out_of_range"},{"operation":"o1","pointer":"/operations/1","code":"sale_ends_before_release"},{"code":"too_many_errors"}]""",
            errors.GetRawText());
        Assert.Equal(issues.Select(issue => (issue.Operation, issue.Location, issue.Member, issue.Code)), read.Select(issue => (issue.Operation, issue.Location, issue.Member, issue.Code)));
    }

    [Fact]
    public void Validation_issues_need_a_code()
    {
        using var document = JsonDocument.Parse("""[{"operation":"o0"}]""");

        Assert.Throws<JsonException>(() => BatchJson.ReadIssues(document.RootElement));
    }
}

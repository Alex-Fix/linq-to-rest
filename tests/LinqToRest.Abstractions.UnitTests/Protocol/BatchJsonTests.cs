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
}

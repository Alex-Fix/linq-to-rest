using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using LinqToRest.Benchmarks.Client;
using LinqToRest.Protocol;

namespace LinqToRest.Benchmarks;

/// <summary>Wire values: JsonValues.Write and JsonValues.Read per kind.</summary>
public class JsonValuesCodec
{
    private readonly ArrayBufferWriter<byte> _buffer = new(256);
    private object _value = 0;
    private Type _type = typeof(int);
    private JsonElement _element;

    [Params("int", "decimal", "string", "datetime", "guid", "enum", "list")]
    public string Kind { get; set; } = "int";

    [GlobalSetup]
    public void Setup()
    {
        (_value, _type) = Kind switch
        {
            "int" => ((object)42, typeof(int)),
            "decimal" => (19.99m, typeof(decimal)),
            "string" => ("Chess board", typeof(string)),
            "datetime" => (new DateTime(2026, 10, 18, 13, 44, 0, DateTimeKind.Utc), typeof(DateTime)),
            "guid" => (Guid.NewGuid(), typeof(Guid)),
            "enum" => (ProductStatus.Active, typeof(ProductStatus)),
            _ => (new List<string> { "sale", "new", "classic" }, typeof(List<string>)),
        };
        _element = JsonValues.ToElement(_value);
    }

    [Benchmark]
    public int Write()
    {
        _buffer.ResetWrittenCount();
        using var writer = new Utf8JsonWriter(_buffer);
        JsonValues.Write(writer, _value);
        writer.Flush();
        return _buffer.WrittenCount;
    }

    [Benchmark]
    public object? Read() => JsonValues.Read(_element, _type, "/x");
}

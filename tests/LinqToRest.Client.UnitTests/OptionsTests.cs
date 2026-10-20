using System.Text.Json.Serialization;

namespace LinqToRest.Client.UnitTests;

public sealed class OptionsTests
{
    [Fact]
    public void Serializer_options_are_shared_by_contexts_and_read_only()
    {
        var (first, _) = Shop.Create();
        var (second, _) = Shop.Create();

        var options = first.Options.SerializerOptions;
        Assert.Same(options, second.Options.SerializerOptions);
        Assert.True(options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => options.Converters.Add(new JsonStringEnumConverter()));
    }
}

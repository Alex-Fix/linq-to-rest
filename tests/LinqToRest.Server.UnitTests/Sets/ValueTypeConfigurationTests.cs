using LinqToRest.Modeling;
using LinqToRest.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using static LinqToRest.Server.UnitTests.Sets.SetConfigurationTests;

namespace LinqToRest.Server.UnitTests.Sets;

public sealed class ValueTypeConfigurationTests
{
    [Fact]
    public void Value_types_are_exported_once_and_used_by_set_properties()
    {
        var model = Build(o => AddValueTypes(o)
            .AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.All).Value(p => p.Size).HasName("dimensions"))
            .AddSet<Supplier>(b => b.ToSet("suppliers").AllowWrites(WriteOperations.Update).Value(s => s.Contact))
            .AddSet<Line>(b => b.ToSet("lines").Value(l => l.Shipping).IsNotFilterable())).Model;

        Assert.Equal(["Contact", "Dimensions", "Location", "Weight"], model.ValueTypes.Select(valueType => valueType.Name));
        Assert.True(model.FindSet("products")!.FindProperty("dimensions") is { Type: "Dimensions", IsNullable: false, IsWritable: true });
        Assert.True(model.FindSet("suppliers")!.FindProperty("contact") is { Type: "Contact", IsNullable: true, IsWritable: true });
        Assert.True(model.FindSet("lines")!.FindProperty("shipping") is { Type: "Location", IsNullable: true, IsWritable: false, IsFilterable: false });

        var dimensions = model.FindValueType("Dimensions")!;
        Assert.Equal(["width", "weight"], dimensions.Properties.Select(property => property.Name));
        Assert.True(dimensions.FindProperty("width") is { Type: "float64", IsSortable: true, IsWritable: false, Rules.Min: 0d });
        Assert.True(dimensions.FindProperty("weight") is { Type: "Weight", IsNullable: false, IsWritable: false });
        Assert.True(model.FindValueType("Weight")!.FindProperty("unit") is { Type: "string", IsNullable: false, Rules.MaxLength: 5 });
        Assert.True(model.FindValueType("Location")!.FindProperty("city") is { IsNullable: true });
        Assert.True(model.FindValueType("Contact")!.FindProperty("phone") is { IsFilterable: false, IsNullable: true });
    }

    [Fact]
    public void One_value_type_serves_complex_and_owned_uses()
    {
        var server = Build(o => AddValueTypes(o)
            .AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.Insert).Value(p => p.Origin))
            .AddSet<Line>(b => b.ToSet("lines").Value(l => l.Shipping)));

        var origin = server.FindSet("products")!.FindProperty("origin")!;
        var shipping = server.FindSet("lines")!.FindProperty("shipping")!;
        Assert.Same(origin.ValueType, shipping.ValueType);
        Assert.True(origin is { IsNullable: true, IsWritable: true, EntityProperty.Name: "Origin" });
        Assert.Single(server.Model.ValueTypes);
    }

    [Fact]
    public void Writable_values_are_constructed_through_a_constructor_or_setters()
    {
        var server = Build(o => AddValueTypes(o).AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.Update).Value(p => p.Size)));

        var dimensions = server.FindSet("products")!.FindProperty("size")!.ValueType!;
        var weight = dimensions.FindProperty("weight")!.ValueType!;
        Assert.True(dimensions.Construction is { Arguments: [], Setters: [{ Name: "width" }, { Name: "weight" }] });
        Assert.Empty(dimensions.Construction.Constructor.GetParameters());
        Assert.True(weight.Construction is { Arguments: [{ Name: "amount" }, { Name: "unit" }], Setters: [] });
        Assert.True(dimensions.FindProperty("width") is { IsWritable: false, Binding.Parameters: [{ Type.Name: "Dimensions" }] });
    }

    [Fact]
    public void Read_only_uses_need_neither_every_member_nor_a_constructor()
    {
        var server = Build(o => o
            .AddValueType<Badge>(b => b.Property(x => x.Text))
            .AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.Update).Value(p => p.Badge).IsReadOnly()));

        var badge = server.FindSet("products")!.FindProperty("badge")!;
        Assert.True(badge is { IsWritable: false, ValueType.Construction: null });
    }

    [Fact]
    public void Value_types_come_from_the_assembly_scan()
    {
        var builder = (LinqToRestServerBuilder?)null;
        new ServiceCollection().AddLinqToRest(o =>
        {
            builder = o;
            o.UseDbContext<ShopContext>().AddSetsFromAssembly(typeof(PlaceType).Assembly);
        });

        // Ordered by configuration class: the golden shop's (LinqToRest.Server.UnitTests.Golden) come first.
        Assert.Equal(
            [typeof(Golden.Dimensions), typeof(Golden.Weight), typeof(Contact), typeof(Dimensions), typeof(Place), typeof(Weight)],
            builder!.ValueTypes.Select(configure => configure().ClrType));
    }

    [Theory]
    [InlineData("unconfigured", "Badge isn't a configured value type")]
    [InlineData("notComplex", "isn't a complex property or an owned reference")]
    [InlineData("hiddenMember", "doesn't expose 'Unit'")]
    [InlineData("hiddenNested", "doesn't expose 'Weight'")]
    [InlineData("notConstructible", "can't be constructed for writes")]
    [InlineData("unmapped", "exposes 'Area', which isn't a property EF Core maps")]
    [InlineData("twice", "The value type Place is configured twice")]
    [InlineData("propertyAndValue", "configured both with Property(...) and Value(...)")]
    [InlineData("empty", "exposes no members")]
    [InlineData("mappedDifferently", "is mapped differently where it's used")]
    [InlineData("nameClash", "type name 'Product' is used twice")]
    [InlineData("rangeType", "with Int32 bounds, but the property is Double")]
    public void Invalid_value_types_fail_at_startup(string problem, string message)
    {
        var error = Assert.ThrowsAny<Exception>(() => Build(o => ConfigureInvalid(o, problem)));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    private static LinqToRestServerBuilder AddValueTypes(LinqToRestServerBuilder builder) =>
        builder.AddValueType(new DimensionsType()).AddValueType(new WeightType()).AddValueType(new PlaceType()).AddValueType(new ContactType());

    private static void ConfigureInvalid(LinqToRestServerBuilder o, string problem)
    {
        switch (problem)
        {
            case "unconfigured":
                o.AddSet<Product>(b => b.ToSet("products").Value(p => p.Badge));
                break;
            case "notComplex":
                o.AddValueType<Category>(b => b.Property(c => c.Name)).AddSet<Product>(b => b.ToSet("products").Value(p => p.Category));
                break;
            case "hiddenMember":
                o.AddValueType(new DimensionsType()).AddValueType<Weight>(b => b.Property(w => w.Amount))
                    .AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.Insert).Value(p => p.Size));
                break;
            case "hiddenNested":
                o.AddValueType<Dimensions>(b => b.Property(d => d.Width)).AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.Update).Value(p => p.Size));
                break;
            case "notConstructible":
                o.AddValueType<Badge>(b =>
                {
                    b.Property(x => x.Text);
                    b.Property(x => x.Level);
                }).AddSet<Product>(b => b.ToSet("products").AllowWrites(WriteOperations.Update).Value(p => p.Badge));
                break;
            case "unmapped":
                o.AddValueType<Dimensions>(b => b.Property(d => d.Area)).AddSet<Product>(b => b.ToSet("products").Value(p => p.Size));
                break;
            case "twice":
                AddValueTypes(o).AddValueType<Place>(b => b.Property(p => p.Country)).AddSet<Product>(b => b.ToSet("products").Value(p => p.Origin));
                break;
            case "propertyAndValue":
                AddValueTypes(o).AddSet<Product>(b =>
                {
                    b.ToSet("products").Value(p => p.Origin);
                    b.Property(p => p.Origin);
                });
                break;
            case "empty":
                o.AddValueType<Place>(_ => { }).AddSet<Product>(b => b.ToSet("products").Value(p => p.Origin));
                break;
            case "mappedDifferently":
                AddValueTypes(o)
                    .AddSet<Product>(b => b.ToSet("products").Value(p => p.Origin))
                    .AddSet<Category>(b => b.ToSet("categories").Value(c => c.Address));
                break;
            case "nameClash":
                o.AddValueType<Place>(b => b.HasTypeName("Product").Property(p => p.Country)).AddSet<Product>(b => b.ToSet("products").Value(p => p.Origin));
                break;
            case "rangeType":
                o.AddValueType<Dimensions>(b =>
                {
                    b.Property(d => d.Width).HasMin(0);
                    b.Value(d => d.Weight);
                }).AddValueType(new WeightType()).AddSet<Product>(b => b.ToSet("products").Value(p => p.Size));
                break;
        }
    }
}

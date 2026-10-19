using LinqToRest.Modeling;

namespace LinqToRest.Abstractions.UnitTests.Modeling;

public sealed class DtoModelBuilderTests
{
    private static DtoModelBuilder ShopBuilder(Action<DtoTypeBuilder<ProductDto>>? product = null) =>
        new DtoModelBuilder()
            .Dto<ProductDto>(b =>
            {
                b.ToSet("products").HasKey(d => d.Id).HasConcurrencyToken(d => d.Version);
                b.HasOne(d => d.Category).WithForeignKey(d => d.CategoryId);
                b.HasMany(d => d.Lines);
                b.Property(d => d.CategoryName).IsReadOnly();
                b.Property(d => d.Price).IsSortable();
                product?.Invoke(b);
            })
            .Dto<CategoryDto>(b => b.ToSet("categories").HasKey(d => d.Id))
            .Dto<OrderLineDto>(b => b.ToSet("orderLines").HasKey(d => new { d.OrderId, d.LineNumber }));

    [Fact]
    public void Builds_properties_navigations_key_and_token_with_wire_names()
    {
        var product = ShopBuilder().Build().FindSet("products")!;

        Assert.Equal(typeof(ProductDto), product.ClrType);
        Assert.Equal(
            ["id", "name", "description", "price", "status", "version", "categoryId", "tags", "categoryName"],
            product.Properties.Select(p => p.WireName));
        Assert.Equal(["category", "lines"], product.Navigations.Select(n => n.WireName));
        Assert.Equal("id", Assert.Single(product.Key).WireName);
        Assert.Equal("version", product.ConcurrencyToken!.WireName);
        Assert.Equal("categoryId", product.FindNavigation("category")!.ForeignKey!.WireName);
    }

    [Fact]
    public void Derives_access_flags_and_nullability()
    {
        var product = ShopBuilder(b => b.Property(d => d.Description).IsNotFilterable()).Build().FindSet("products")!;

        Assert.False(product.FindProperty("id")!.IsWritable);
        Assert.True(product.FindProperty("id")!.IsSortable);
        Assert.False(product.FindProperty("version")!.IsWritable);
        Assert.False(product.FindProperty("categoryName")!.IsWritable);
        Assert.True(product.FindProperty("name")!.IsWritable);
        Assert.False(product.FindProperty("name")!.IsSortable);
        Assert.True(product.FindProperty("price")!.IsSortable);
        Assert.False(product.FindProperty("description")!.IsFilterable);
        Assert.True(product.FindProperty("description")!.IsNullable);
        Assert.False(product.FindProperty("name")!.IsNullable);
        Assert.True(product.FindProperty("tags")!.IsCollection);
        Assert.Equal(typeof(string), product.FindProperty("tags")!.ElementType);
        Assert.True(product.FindNavigation("category")!.IsNullable);
        Assert.True(product.FindNavigation("lines")!.IsCollection);
    }

    [Fact]
    public void Supports_composite_keys_and_default_pagination()
    {
        var lines = ShopBuilder().Build().FindSet("orderLines")!;

        Assert.Equal(["orderId", "lineNumber"], lines.Key.Select(p => p.WireName));
        Assert.Equal(PaginationModes.All, lines.Pagination.Modes);
        Assert.Equal(50, lines.Pagination.DefaultPageSize);
        Assert.Equal(200, lines.Pagination.MaxPageSize);
        Assert.Equal(10_000, lines.Pagination.MaxSkip);
    }

    [Fact]
    public void Applies_configurations_from_assembly()
    {
        var model = new DtoModelBuilder().ApplyConfigurationsFromAssembly(typeof(CategoryConfiguration).Assembly).Build();

        Assert.Same(model.FindSet("categories"), model.FindType(typeof(CategoryDto)));
    }

    [Fact]
    public void Ignored_properties_are_left_out()
    {
        var product = ShopBuilder(b => b.Ignore(d => d.Description)).Build().FindSet("products")!;

        Assert.Null(product.FindProperty("description"));
    }

    [Fact]
    public void Missing_key_is_rejected() =>
        AssertInvalid(new DtoModelBuilder().Dto<CategoryDto>(b => b.ToSet("categories")), "has no key");

    [Fact]
    public void Missing_set_name_is_rejected() =>
        AssertInvalid(new DtoModelBuilder().Dto<CategoryDto>(b => b.HasKey(d => d.Id)), "has no set name");

    [Theory]
    [InlineData("Products")]
    [InlineData("pro-ducts")]
    [InlineData("1products")]
    public void Invalid_set_name_is_rejected(string name) =>
        AssertInvalid(new DtoModelBuilder().Dto<CategoryDto>(b => b.ToSet(name).HasKey(d => d.Id)), "invalid set name");

    [Fact]
    public void Duplicate_set_name_is_rejected() =>
        AssertInvalid(
            new DtoModelBuilder()
                .Dto<CategoryDto>(b => b.ToSet("things").HasKey(d => d.Id))
                .Dto<OrderLineDto>(b => b.ToSet("things").HasKey(d => d.OrderId)),
            "already uses");

    [Fact]
    public void Navigation_to_unconfigured_dto_is_rejected() =>
        AssertInvalid(
            new DtoModelBuilder().Dto<ProductDto>(b => b.ToSet("products").HasKey(d => d.Id)),
            "isn't configured in the model");

    [Fact]
    public void Unsupported_property_type_is_rejected() =>
        AssertInvalid(new DtoModelBuilder().Dto<UnsupportedDto>(b => b.ToSet("things").HasKey(d => d.Id)), "unsupported type");

    [Fact]
    public void Nullable_key_is_rejected() =>
        AssertInvalid(ShopBuilder(b => b.HasKey(d => d.Description)), "non-nullable scalars");

    [Fact]
    public void Sortable_collection_is_rejected() =>
        AssertInvalid(ShopBuilder(b => b.Property(d => d.Tags).IsSortable()), "can't be sorted");

    [Fact]
    public void Foreign_key_of_wrong_type_is_rejected() =>
        AssertInvalid(ShopBuilder(b => b.HasOne(d => d.Category).WithForeignKey(d => d.Name)), "same type as the single key");

    [Fact]
    public void Default_page_size_above_maximum_is_rejected() =>
        AssertInvalid(ShopBuilder(b => b.HasPagination(p => p.DefaultPageSize(300))), "DefaultPageSize <= MaxPageSize");

    [Fact]
    public void Pagination_without_modes_is_rejected() =>
        AssertInvalid(ShopBuilder(b => b.HasPagination(p => p.Modes(PaginationModes.None))), "pagination mode");

    [Fact]
    public void Empty_model_is_rejected() => AssertInvalid(new DtoModelBuilder(), "no types");

    [Fact]
    public void Non_property_key_expression_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new DtoModelBuilder().Dto<CategoryDto>(b => b.HasKey(d => d.Id + 1)));

    private static void AssertInvalid(DtoModelBuilder builder, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(builder.Build);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    public sealed class UnsupportedDto : IDto
    {
        public int Id { get; set; }
        public Uri? Link { get; set; }
    }
}

using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using RestToLinq.Modeling;
using RestToLinq.Server.Hosting;
using RestToLinq.Server.Mapping;
using RestToLinq.Server.Policies;

namespace RestToLinq.Server.UnitTests.Mapping;

public sealed class MappingValidationTests
{
    [Fact]
    public void Valid_mappings_build_bindings_and_navigations()
    {
        var model = Register(o => AddShop(o));

        var products = model.FindSet("products")!;
        var label = products.DtoType.FindProperty("label")!;
        Assert.Equal(typeof(Product), products.EntityType);
        Assert.Equal(["Id", "Name", "Version", "CategoryId"], products.EntityProperties.Values.Select(p => p.Name));
        Assert.True(products.Bindings.ContainsKey(label));
        Assert.False(products.EntityProperties.ContainsKey(label));
        var category = products.Navigations[products.DtoType.FindNavigation("category")!];
        Assert.Equal(nameof(Product.Category), category.EntityProperty!.Name);
        Assert.Same(model.FindSet("categories"), category.Target);
    }

    [Fact]
    public void Computed_writable_member_is_rejected() =>
        AssertInvalid(
            Mapping(p => new ProductDto { Id = p.Id, Name = p.Name.ToUpperInvariant(), Label = p.Name, Version = p.Version, CategoryId = p.CategoryId }),
            "computes the writable member 'Name'");

    [Fact]
    public void Computed_key_is_rejected() =>
        AssertInvalid(
            Mapping(p => new ProductDto { Id = p.Id + 1, Name = p.Name, Label = p.Name, Version = p.Version, CategoryId = p.CategoryId }),
            "computes the key 'Id'");

    [Fact]
    public void Writable_member_of_another_type_is_rejected() =>
        AssertInvalid(
            Mapping(p => new ProductDto { Id = p.Id, Name = p.Name, Label = p.Name, Version = p.Version, CategoryId = (int)p.Stock }),
            "computes the writable member 'CategoryId'");

    [Fact]
    public void Missing_member_is_rejected() =>
        AssertInvalid(
            Mapping(p => new ProductDto { Id = p.Id, Name = p.Name, Version = p.Version, CategoryId = p.CategoryId }),
            "doesn't assign 'Label'");

    [Fact]
    public void Navigation_in_projection_is_rejected() =>
        AssertInvalid(
            Mapping(p => new ProductDto { Id = p.Id, Name = p.Name, Label = p.Name, Version = p.Version, CategoryId = p.CategoryId, Category = null }),
            "assigns the navigation 'Category'");

    [Fact]
    public void Non_initializer_projection_is_rejected() =>
        AssertInvalid(Mapping(_ => new ProductDto()), "member initializer", configure: false);

    [Fact]
    public void Unmapped_navigation_is_rejected() =>
        AssertInvalid(
            Mapping(p => new ProductDto { Id = p.Id, Name = p.Name, Label = p.Name, Version = p.Version, CategoryId = p.CategoryId }, b => b.HasOne(d => d.Category, p => p.Category)),
            "doesn't map the navigation 'Lines'");

    [Fact]
    public void Navigation_to_wrong_entity_is_rejected() =>
        AssertInvalid(
            Mapping(
                p => new ProductDto { Id = p.Id, Name = p.Name, Label = p.Name, Version = p.Version, CategoryId = p.CategoryId },
                b => b.HasOne(d => d.Category, p => p.Category).HasMany(d => d.Lines, _ => new List<Category>())),
            "but LineDto is mapped from Line");

    [Fact]
    public void Missing_mapping_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Register(o => o.AddMapping(new CategoryMapping()).AddMapping(new LineMapping())));

        Assert.Contains("DTO ProductDto has no mapping", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_mapping_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            Register(o => AddShop(o).AddMapping(new LineMapping())));

        Assert.Contains("is mapped twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_db_context_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRestToLinq(ShopModel.Create(), o => AddShop(o)));

        Assert.Contains("UseDbContext", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_from_another_db_context_is_rejected() =>
        AssertInvalid(
            new InlineMapping(b => b.FromSource<OtherContext>(db => db.Set<Product>())
                .Project(p => new ProductDto { Id = p.Id, Name = p.Name, Label = p.Name, Version = p.Version, CategoryId = p.CategoryId })
                .HasOne(d => d.Category, p => p.Category)
                .HasMany(d => d.Lines, p => p.Lines)),
            "reads from OtherContext");

    [Fact]
    public void Mapping_for_dto_outside_the_model_is_rejected()
    {
        var model = new DtoModelBuilder().Dto<CategoryDto>(b => b.ToSet("categories").HasKey(d => d.Id)).Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRestToLinq(model, o => o.UseDbContext<ShopContext>().AddMapping(new LineMapping())));

        Assert.Contains("isn't part of the DTO model", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Writable_member_without_public_setter_is_rejected()
    {
        var model = new DtoModelBuilder()
            .Dto<PriceDto>(b => b.ToSet("prices").HasKey(d => d.Id))
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddRestToLinq(model, o => o.UseDbContext<ShopContext>().AddMapping(new PriceMapping())));

        Assert.Contains("has no public setter", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Policy_must_derive_from_dto_set_policy()
    {
        var builder = (RestToLinqServerBuilder?)null;
        new ServiceCollection().AddRestToLinq(ShopModel.Create(), o =>
        {
            builder = o;
            AddShop(o.UseDbContext<ShopContext>()).AddPolicy<ProductPolicy>();
        });

        Assert.Throws<ArgumentException>(() => builder!.AddPolicy<string>());
        Assert.Contains(builder!.Services, service => service.ServiceType == typeof(DtoSetPolicy<ProductDto, Product>));
    }

    [Fact]
    public void Assembly_scan_finds_mappings_with_parameterless_constructors()
    {
        var builder = (RestToLinqServerBuilder?)null;
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddRestToLinq(ShopModel.Create(), o =>
        {
            builder = o;
            o.UseDbContext<ShopContext>().AddMappingsFromAssembly(typeof(ProductMapping).Assembly);
        }));

        Assert.Equal(
            [typeof(CategoryDto), typeof(LineDto), typeof(PriceDto), typeof(ProductDto)],
            builder!.Mappings.Select(mapping => mapping.DtoType).OrderBy(type => type.Name));
    }

    private static RestToLinqServerBuilder AddShop(RestToLinqServerBuilder builder) =>
        builder.AddMapping(new ProductMapping()).AddMapping(new CategoryMapping()).AddMapping(new LineMapping());

    private static ServerModel Register(Action<RestToLinqServerBuilder> configure)
    {
        var services = new ServiceCollection().AddRestToLinq(ShopModel.Create(), o => configure(o.UseDbContext<ShopContext>()));
        return (ServerModel)services.Single(service => service.ServiceType == typeof(ServerModel)).ImplementationInstance!;
    }

    private static InlineMapping Mapping(Expression<Func<Product, ProductDto>> projection, Action<DtoMappingBuilder<ProductDto, Product>>? navigations = null) =>
        new(b =>
        {
            b.Project(projection);
            if (navigations is null)
            {
                b.HasOne(d => d.Category, p => p.Category).HasMany(d => d.Lines, p => p.Lines);
            }
            else
            {
                navigations(b);
            }
        });

    private static void AssertInvalid(InlineMapping mapping, string expected, bool configure = true)
    {
        _ = configure;
        var error = Assert.Throws<InvalidOperationException>(() =>
            Register(o => o.AddMapping(mapping).AddMapping(new CategoryMapping()).AddMapping(new LineMapping())));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    public sealed class InlineMapping(Action<DtoMappingBuilder<ProductDto, Product>> configure) : IDtoMapping<ProductDto, Product>
    {
        public void Configure(DtoMappingBuilder<ProductDto, Product> builder) => configure(builder);
    }

    public sealed class PriceDto : IDto
    {
        public int Id { get; set; }
        public decimal Price { get; set; }
    }

    public sealed class PriceMapping : IDtoMapping<PriceDto, Product>
    {
        public void Configure(DtoMappingBuilder<PriceDto, Product> builder) => builder.Project(p => new PriceDto { Id = p.Id, Price = p.Price });
    }

    public sealed class ProductPolicy : DtoSetPolicy<ProductDto, Product>;
}

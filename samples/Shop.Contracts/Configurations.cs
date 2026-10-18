using RestToLinq.Modeling;

namespace Shop.Contracts;

public sealed class ProductConfiguration : IDtoTypeConfiguration<ProductDto>
{
    public void Configure(DtoTypeBuilder<ProductDto> builder)
    {
        builder.ToSet("products").HasKey(d => d.Id).HasConcurrencyToken(d => d.Version);
        builder.HasOne(d => d.Category).WithForeignKey(d => d.CategoryId);

        // Sorting is opt-in: only indexed columns.
        builder.Property(d => d.Price).IsSortable();
        builder.Property(d => d.Name).IsSortable();

        // Computed on the server.
        builder.Property(d => d.Tags).IsReadOnly();
        builder.Property(d => d.CategoryName).IsReadOnly();
        builder.HasPagination(p => p.DefaultPageSize(20).MaxPageSize(100));
    }
}

public sealed class CategoryConfiguration : IDtoTypeConfiguration<CategoryDto>
{
    public void Configure(DtoTypeBuilder<CategoryDto> builder) => builder.ToSet("categories").HasKey(d => d.Id);
}

public static class ShopModel
{
    public static DtoModel Create() => new DtoModelBuilder().ApplyConfigurationsFromAssembly(typeof(ShopModel).Assembly).Build();
}

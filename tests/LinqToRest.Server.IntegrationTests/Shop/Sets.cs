using LinqToRest.Modeling;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.Sets;

namespace LinqToRest.Server.IntegrationTests.Shop;

public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products").AllowWrites(WriteOperations.All);
        builder.Property(p => p.Name).HasMaxLength(100);
        builder.Property(p => p.Description);
        builder.Property(p => p.Price).IsSortable().HasMin(0m).IsAggregatable();
        builder.Property(p => p.Status).IsSortable().IsGroupable();
        builder.HasConcurrencyToken(p => p.Version);
        builder.Property(p => p.CreatedAt).IsSortable();
        builder.Property(p => p.ReleasedOn).IsSortable();
        builder.Property(p => p.Rating).IsNotFilterable().IsAggregatable();
        builder.Property(p => p.IsFeatured);
        builder.Property(p => p.CategoryId).IsGroupable();
        builder.Value(p => p.Size);
        builder.Value(p => p.Origin);
        builder.Computed("tags", p => p.Tags.OrderBy(t => t.Value).Select(t => t.Value).ToList());
        builder.Computed("categoryName", p => p.Category != null ? p.Category.Name : null);
        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasMany(p => p.Lines);
        builder.HasPagination(p => p.DefaultPageSize(10).MaxPageSize(20).MaxSkip(100));
    }
}

public sealed class CategorySet : IEntitySetConfiguration<Category>
{
    public void Configure(EntitySetBuilder<Category> builder)
    {
        builder.ToSet("categories").AllowWrites(WriteOperations.Insert | WriteOperations.Update);
        builder.Property(c => c.Name).IsSortable().IsGroupable();
        builder.Value(c => c.Contact);
        builder.HasMany(c => c.Products);
        builder.HasPagination(p => p.Modes(PaginationModes.Cursor));
    }
}

public sealed class OrderLineSet : IEntitySetConfiguration<OrderLine>
{
    public void Configure(EntitySetBuilder<OrderLine> builder)
    {
        builder.ToSet("orderLines");
        builder.Property(l => l.ProductId);
        builder.Property(l => l.Sku);
        builder.Property(l => l.Quantity);
        builder.Property(l => l.UnitPrice);
        builder.Value(l => l.Shipping).IsNotFilterable();
    }
}

public sealed class DimensionsType : IValueTypeConfiguration<Dimensions>
{
    public void Configure(ValueTypeBuilder<Dimensions> builder)
    {
        builder.Property(d => d.Width).IsSortable().HasMin(0d);
        builder.Property(d => d.Height).HasMin(0d);
        builder.Value(d => d.Weight);
    }
}

public sealed class WeightType : IValueTypeConfiguration<Weight>
{
    public void Configure(ValueTypeBuilder<Weight> builder)
    {
        builder.Property(w => w.Amount).IsSortable().IsAggregatable().HasMin(0d);
        builder.Property(w => w.Unit).IsGroupable().HasMaxLength(5);
    }
}

public sealed class PlaceType : IValueTypeConfiguration<Place>
{
    public void Configure(ValueTypeBuilder<Place> builder)
    {
        builder.Property(p => p.Country).IsSortable().IsGroupable().HasPattern("^[A-Z]{2}$");
        builder.Property(p => p.City);
    }
}

public sealed class ContactType : IValueTypeConfiguration<Contact>
{
    public void Configure(ValueTypeBuilder<Contact> builder)
    {
        builder.Property(c => c.Email).IsRequired().HasMaxLength(100);
        builder.Property(c => c.Phone).IsNotFilterable();
    }
}

public static class ShopValueTypes
{
    /// <summary>Adds the shop's value types, for tests that register sets one by one.</summary>
    public static void AddShopValueTypes(this LinqToRestServerBuilder builder) =>
        builder.AddValueType(new DimensionsType()).AddValueType(new WeightType()).AddValueType(new PlaceType()).AddValueType(new ContactType());
}

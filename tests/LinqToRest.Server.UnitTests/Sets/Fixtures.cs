using System.ComponentModel.DataAnnotations.Schema;
using LinqToRest.Modeling;
using LinqToRest.Server.Sets;
using Microsoft.EntityFrameworkCore;

namespace LinqToRest.Server.UnitTests.Sets;

public enum Status
{
    Draft,
    Active,
}

public sealed class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long Stock { get; set; }
    public decimal Price { get; private init; }
    public Status Status { get; set; }
    public Guid Version { get; set; }
    public string Secret { get; set; } = "";
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    public List<Line> Lines { get; set; } = [];
    public Dimensions Size { get; set; } = new() { Width = 1, Weight = new Weight(1, "kg") };
    public Place? Origin { get; set; }
    public Badge? Badge { get; set; }

    [NotMapped]
    public string Label => Name + "!";
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Place? Address { get; set; }
    public List<Product> Products { get; set; } = [];
}

public sealed class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public Contact? Contact { get; set; }
}

public sealed class Line
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public Place? Shipping { get; set; }
}

public sealed record Dimensions
{
    public required double Width { get; init; }
    public required Weight Weight { get; init; }
    public double Area => Width * Width;
}

public sealed record Weight(double Amount, string Unit);

public sealed record Place(string Country, string? City);

public sealed class Contact
{
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
}

/// <summary>EF Core can build it through its constructor and private setter; batches can't.</summary>
public sealed class Badge(string text)
{
    public string Text { get; } = text;
    public int Level { get; private set; }

    public void Promote() => Level++;
}

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Version).IsConcurrencyToken();
            product.ComplexProperty(p => p.Size, size => size.ComplexProperty(d => d.Weight));
            product.ComplexProperty(p => p.Origin);
            product.ComplexProperty(p => p.Badge, badge =>
            {
                badge.Property(b => b.Text);
                badge.Property(b => b.Level);
            });
        });
        modelBuilder.Entity<Supplier>().OwnsOne(s => s.Contact);
        modelBuilder.Entity<Category>().OwnsOne(c => c.Address, address => address.Property(a => a.City).IsRequired());
        modelBuilder.Entity<Line>().OwnsOne(l => l.Shipping, shipping => shipping.ToJson());
    }
}

public sealed class OtherContext(DbContextOptions<OtherContext> options) : DbContext(options);

public sealed class ProductSet : IEntitySetConfiguration<Product>
{
    public void Configure(EntitySetBuilder<Product> builder)
    {
        builder.ToSet("products").AllowWrites(WriteOperations.Insert | WriteOperations.Update);
        builder.FromSource<ShopContext>(db => db.Products.Where(p => p.Id > 0));
        builder.Property(p => p.Name).IsSortable();
        builder.Property(p => p.Description).IsNotFilterable();
        builder.Property(p => p.Price).IsReadOnly();
        builder.Property(p => p.Status);
        builder.HasConcurrencyToken(p => p.Version);
        builder.Property(p => p.CategoryId);
        builder.Computed("label", p => p.Name + "!").IsRequired();
        builder.Computed("lineCount", p => p.Lines.Count);
        builder.Computed("supplierName", p => p.Supplier!.Name);
        builder.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
        builder.HasOne(p => p.Supplier).HasName("vendor");
        builder.HasMany(p => p.Lines);
    }
}

public sealed class CategorySet : IEntitySetConfiguration<Category>
{
    public void Configure(EntitySetBuilder<Category> builder)
    {
        builder.ToSet("categories");
        builder.Property(c => c.Name);
        builder.HasMany(c => c.Products);
        builder.HasPagination(p => p.Modes(PaginationModes.Cursor).DefaultPageSize(5).MaxPageSize(10));
    }
}

public sealed class SupplierSet : IEntitySetConfiguration<Supplier>
{
    public void Configure(EntitySetBuilder<Supplier> builder) => builder.ToSet("suppliers").Property(s => s.Name);
}

public sealed class LineSet : IEntitySetConfiguration<Line>
{
    public void Configure(EntitySetBuilder<Line> builder) => builder.ToSet("lines").Property(l => l.Quantity);
}

public sealed class DimensionsType : IValueTypeConfiguration<Dimensions>
{
    public void Configure(ValueTypeBuilder<Dimensions> builder)
    {
        builder.Property(d => d.Width).IsSortable().HasMin(0d);
        builder.Value(d => d.Weight);
    }
}

public sealed class WeightType : IValueTypeConfiguration<Weight>
{
    public void Configure(ValueTypeBuilder<Weight> builder)
    {
        builder.Property(w => w.Amount).IsAggregatable();
        builder.Property(w => w.Unit).HasMaxLength(5);
    }
}

public sealed class PlaceType : IValueTypeConfiguration<Place>
{
    public void Configure(ValueTypeBuilder<Place> builder)
    {
        builder.HasTypeName("Location");
        builder.Property(p => p.Country).IsGroupable();
        builder.Property(p => p.City);
    }
}

public sealed class ContactType : IValueTypeConfiguration<Contact>
{
    public void Configure(ValueTypeBuilder<Contact> builder)
    {
        builder.Property(c => c.Email).IsRequired();
        builder.Property(c => c.Phone).IsNotFilterable();
    }
}

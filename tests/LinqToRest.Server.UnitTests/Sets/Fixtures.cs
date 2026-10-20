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

    [NotMapped]
    public string Label => Name + "!";
}

public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Product> Products { get; set; } = [];
}

public sealed class Supplier
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class Line
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}

public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Product>().Property(p => p.Version).IsConcurrencyToken();
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

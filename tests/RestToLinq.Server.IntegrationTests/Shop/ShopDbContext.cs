using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RestToLinq.Server.IntegrationTests.Shop;

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // SQLite has no rowversion, so the app rotates the concurrency token itself.
        foreach (var entry in ChangeTracker.Entries<Product>().Where(entry => entry.State == EntityState.Modified))
        {
            entry.Entity.Version = Guid.NewGuid();
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Points identity sequences past explicitly seeded ids, so later inserts don't collide (PostgreSQL).</summary>
    public void ResetSequences()
    {
        if (!Database.IsNpgsql())
        {
            return;
        }

        foreach (var entity in Model.GetEntityTypes())
        {
            if (entity.FindPrimaryKey()?.Properties is not [{ ValueGenerated: ValueGenerated.OnAdd } key] || entity.GetTableName() is not { } table)
            {
                continue;
            }

            // Names come from the EF model, never from input.
            var column = key.GetColumnName();
            var sql = $"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{column}'), (SELECT MAX(\"{column}\") FROM \"{table}\"))";
            Database.ExecuteSqlRaw(sql);
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Version).IsConcurrencyToken();
            product.HasMany(p => p.Tags).WithOne().HasForeignKey(t => t.ProductId);
            product.HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.ProductId);
        });

        if (Database.IsSqlite())
        {
            // SQLite stores decimal as text, which it can't compare, sort or sum; the double conversion keeps those
            // operations in SQL. PostgreSQL keeps native numeric.
            modelBuilder.Entity<Product>().Property(p => p.Price).HasConversion<double>();
            modelBuilder.Entity<OrderLine>().Property(l => l.UnitPrice).HasConversion<double>();
        }
    }
}

/// <summary>
/// The recommended setup for timestamptz: values are UTC instants, and offset-less values (Kind Unspecified) are
/// taken as UTC. EF Core applies it to parameters compared with the column too.
/// </summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime(),
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

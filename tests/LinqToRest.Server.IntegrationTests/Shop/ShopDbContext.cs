using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LinqToRest.Server.IntegrationTests.Shop;

public sealed class ShopDbContext(DbContextOptions<ShopDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Bin> Bins => Set<Bin>();
    public DbSet<Label> Labels => Set<Label>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        // SQLite has no rowversion, so the app rotates the concurrency token itself.
        foreach (var entry in ChangeTracker.Entries<Product>().Where(entry => entry.State == EntityState.Modified))
        {
            entry.Entity.Version = Guid.NewGuid();
        }

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Points key sequences past explicitly seeded ids, so later inserts don't collide (PostgreSQL, SQL Server).</summary>
    public void ResetSequences()
    {
        if (!Database.IsNpgsql() && !Database.IsSqlServer())
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
            var sql = Database.IsNpgsql()
                ? $"SELECT setval(pg_get_serial_sequence('\"{table}\"', '{column}'), (SELECT MAX(\"{column}\") FROM \"{table}\"))"
                : $"DECLARE @next bigint = (SELECT ISNULL(MAX([{column}]), 0) + 1 FROM [{table}]); EXEC('ALTER SEQUENCE [{KeySequence(table)}] RESTART WITH ' + @next)";
            Database.ExecuteSqlRaw(sql);
        }
    }

    private static string KeySequence(string table) => $"{table}Sequence";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(product =>
        {
            product.Property(p => p.Version).IsConcurrencyToken();
            product.HasMany(p => p.Tags).WithOne().HasForeignKey(t => t.ProductId);
            product.HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.ProductId);
            product.ComplexProperty(p => p.Size, size => size.ComplexProperty(d => d.Weight));
            product.ComplexProperty(p => p.Origin);
            product.HasOne(p => p.Bin).WithMany().HasForeignKey(p => new { p.BinZone, p.BinSlot });
            product.HasMany(p => p.Labels).WithMany().UsingEntity("ProductLabel");
        });
        modelBuilder.Entity<Bin>(bin =>
        {
            bin.HasKey(b => new { b.Zone, b.Slot });
            bin.Property(b => b.Zone).ValueGeneratedOnAdd();
            bin.Property(b => b.Slot).ValueGeneratedOnAdd();
        });
        modelBuilder.Entity<Category>().OwnsOne(c => c.Contact);
        modelBuilder.Entity<OrderLine>().OwnsOne(l => l.Shipping, shipping => shipping.ToJson());

        if (Database.IsSqlite())
        {
            // SQLite stores decimal as text, which it can't compare, sort or sum; the double conversion keeps those
            // operations in SQL. PostgreSQL keeps native numeric.
            modelBuilder.Entity<Product>().Property(p => p.Price).HasConversion<double>();
            modelBuilder.Entity<OrderLine>().Property(l => l.UnitPrice).HasConversion<double>();
        }

        if (Database.IsSqlServer())
        {
            // The seed sets ids, which identity columns refuse (IDENTITY_INSERT is per table); sequences accept them.
            foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(entity => !entity.IsOwned()).ToList())
            {
                if (entity.FindPrimaryKey()?.Properties is [{ ClrType: var type } key] && type == typeof(int) && entity.GetTableName() is { } table)
                {
                    SqlServerPropertyBuilderExtensions.UseSequence(modelBuilder.Entity(entity.Name).Property(key.Name), KeySequence(table));
                }
            }

            modelBuilder.Entity<Product>().Property(p => p.Price).HasPrecision(18, 2);
            modelBuilder.Entity<OrderLine>().Property(l => l.UnitPrice).HasPrecision(18, 2);
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

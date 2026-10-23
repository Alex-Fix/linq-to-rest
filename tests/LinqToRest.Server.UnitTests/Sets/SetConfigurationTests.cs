using LinqToRest.Modeling;
using LinqToRest.Server.Hosting;
using LinqToRest.Server.Mapping;
using LinqToRest.Server.Policies;
using LinqToRest.Server.Sets;
using LinqToRest.Server.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinqToRest.Server.UnitTests.Sets;

public sealed class SetConfigurationTests
{
    [Fact]
    public void Valid_sets_build_the_model_document()
    {
        var model = Build(o => AddShop(o)).Model;

        var products = model.FindSet("products")!;
        Assert.Equal("Product", products.TypeName);
        Assert.Equal(["id"], products.Key);
        Assert.Equal("version", products.ConcurrencyToken);
        Assert.Equal(WriteOperations.Insert | WriteOperations.Update, products.Writes);
        Assert.Equal(
            ["id", "name", "description", "price", "status", "version", "categoryId", "label", "lineCount", "supplierName"],
            products.Properties.Select(property => property.Name));
        Assert.Equal(["category", "vendor", "lines"], products.Navigations.Select(navigation => navigation.Name));
        Assert.Equal(["Status"], model.Enums.Select(enumModel => enumModel.Name));
        Assert.Equal(["categories", "lines", "products", "suppliers"], model.Sets.Select(set => set.Name));
    }

    [Fact]
    public void Member_flags_follow_the_configuration_and_the_EF_model()
    {
        var products = Build(o => AddShop(o)).Model.FindSet("products")!;

        var id = products.FindProperty("id")!;
        Assert.True(id is { Type: "int32", IsSortable: true, IsWritable: false, IsNullable: false });
        Assert.True(products.FindProperty("name") is { Type: "string", IsWritable: true, IsSortable: true, IsNullable: false });
        Assert.True(products.FindProperty("description") is { IsNullable: true, IsFilterable: false, IsWritable: true });
        Assert.True(products.FindProperty("price") is { Type: "decimal", IsWritable: false });
        Assert.True(products.FindProperty("status") is { Type: "Status", IsWritable: true });
        Assert.True(products.FindProperty("version") is { Type: "guid", IsWritable: false });
        Assert.True(products.FindProperty("label") is { Type: "string", IsNullable: false, IsWritable: false });
        Assert.True(products.FindProperty("lineCount") is { Type: "int32", IsNullable: false });
        Assert.True(products.FindProperty("supplierName") is { Type: "string", IsNullable: true });
        Assert.True(products.FindNavigation("category") is { Target: "categories", IsNullable: false, ForeignKey: ["categoryId"] });
        Assert.True(products.FindNavigation("vendor") is { Target: "suppliers", IsNullable: true, ForeignKey: [] });
        Assert.True(products.FindNavigation("lines") is { Target: "lines", IsCollection: true });
    }

    [Fact]
    public void Read_only_sets_have_no_writable_members()
    {
        var categories = Build(o => AddShop(o)).Model.FindSet("categories")!;

        Assert.Equal(WriteOperations.None, categories.Writes);
        Assert.DoesNotContain(categories.Properties, property => property.IsWritable);
        Assert.Equal(PaginationModes.Cursor, categories.Pagination.Modes);
        Assert.Equal(5, categories.Pagination.DefaultPageSize);
    }

    [Fact]
    public void Server_sets_bind_members_to_the_entity()
    {
        var server = Build(o => AddShop(o));

        var products = server.FindSet("products")!;
        Assert.Equal(typeof(Product), products.EntityType);
        Assert.Equal(nameof(Product.Name), products.FindProperty("name")!.EntityProperty!.Name);
        Assert.Null(products.FindProperty("label")!.EntityProperty);
        Assert.Same(products.FindProperty("categoryId"), products.FindNavigation("category")!.ForeignKey);
        Assert.Same(server.FindSet("categories"), products.FindNavigation("category")!.Target);
        Assert.Equal(typeof(SetPolicy<Product>), products.PolicyType);
    }

    [Fact]
    public void Unexposed_members_are_not_in_the_model()
    {
        var products = Build(o => AddShop(o)).Model.FindSet("products")!;

        Assert.Null(products.FindProperty("secret"));
        Assert.Null(products.FindProperty("stock"));
        Assert.Null(products.FindProperty("supplierId"));
    }

    [Fact]
    public void Composite_keys_and_wire_names_are_configurable()
    {
        var model = Build(o => o
            .AddSet<Line>(b => b.ToSet("lines").HasKey(l => new { l.ProductId, l.Id }).HasTypeName("OrderLine"))
            .AddSet<Category>(b => b.ToSet("categories").Property(c => c.Name).HasName("title"))).Model;

        Assert.Equal(["productId", "id"], model.FindSet("lines")!.Key);
        Assert.Equal("OrderLine", model.FindSet("lines")!.TypeName);
        Assert.NotNull(model.FindSet("categories")!.FindProperty("title"));
    }

    [Theory]
    [InlineData("label", "isn't a property mapped by EF Core")]
    [InlineData("category", "isn't a property mapped by EF Core")]
    [InlineData("noSetter", "has no public setter")]
    [InlineData("token", "isn't one in the EF Core model")]
    [InlineData("computedNavigation", "of unsupported type Category")]
    [InlineData("hasOneOnCollection", "isn't a reference navigation")]
    [InlineData("unexposedForeignKey", "it isn't exposed. Call Property(...)")]
    [InlineData("wrongForeignKey", "isn't that navigation's foreign key")]
    [InlineData("noName", "has no name. Call ToSet(...)")]
    [InlineData("badName", "set name 'Products' is invalid")]
    [InlineData("badWireName", "invalid or duplicate member name 'Title'")]
    [InlineData("duplicateWireName", "invalid or duplicate member name 'name'")]
    [InlineData("sortableCollection", "collections and binary values can't be sorted")]
    [InlineData("ruleOnReadOnly", "has validation rules on 'Name', which isn't writable")]
    [InlineData("ruleOnKey", "has validation rules on 'Id', which isn't writable")]
    [InlineData("rangeOfAnotherType", "HasRange on 'Stock' with Int32 bounds, but the property is Int64")]
    [InlineData("rangeOnString", "HasRange on 'Name' with Int32 bounds, but the property is String")]
    [InlineData("minAboveMax", "needs min <= max")]
    [InlineData("lengthOnNumber", "only strings and collections have a length")]
    [InlineData("patternOnNumber", "only strings can have one")]
    [InlineData("nonPortablePattern", "doesn't mean the same in .NET and JavaScript")]
    [InlineData("mustOnUnexposed", "has a Must rule on 'Stock', which isn't exposed")]
    [InlineData("groupableCollection", "collections and binary values can't be group keys")]
    [InlineData("aggregatableString", "only numbers, dates and times can be summed")]
    [InlineData("mustOnReadOnly", "has a Must rule on 'Name', which isn't writable, so it would never run")]
    public void Invalid_product_set_is_rejected(string problem, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(o => o
            .AddSet<Product>(b => ConfigureInvalid(b, problem))
            .AddSet(new CategorySet())
            .AddSet(new SupplierSet())
            .AddSet(new LineSet())));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_rules_are_exported_and_bound()
    {
        var server = Build(o => o
            .AddSet<Product>(b =>
            {
                b.ToSet("products").AllowWrites(WriteOperations.Insert);
                b.Property(p => p.Name).IsRequired().HasMinLength(2).HasMaxLength(50).HasPattern("^[A-Z]");
                b.Property(p => p.Stock).HasMin(0L);
                b.Property(p => p.Description);
            })
            .AddSet(new CategorySet())
            .AddSet(new SupplierSet())
            .AddSet(new LineSet()));

        var products = server.Model.FindSet("products")!;
        Assert.True(products.FindProperty("name")!.Rules is { IsRequired: true, MinLength: 2, MaxLength: 50, Pattern: "^[A-Z]" });
        Assert.True(products.FindProperty("stock")!.Rules is { Min: 0L, Max: null });
        Assert.Null(products.FindProperty("description")!.Rules);
        Assert.Contains("\"rules\": {\"min\": 0}", server.Model.ToJson(), StringComparison.Ordinal);
        Assert.Same(products.FindProperty("name")!.Rules, server.FindSet("products")!.FindProperty("name")!.Rules);
    }

    [Fact]
    public void A_row_rule_may_point_at_a_read_only_member()
    {
        var server = Build(o => o
            .AddSet<Product>(b =>
            {
                b.ToSet("products").AllowWrites(WriteOperations.Insert);
                b.Property(p => p.Name).IsReadOnly();
                b.Must(p => p.Name.Length > 0, "named", p => p.Name);
                b.Must(p => p.Stock >= 0, "in_stock");
            })
            .AddSet(new CategorySet())
            .AddSet(new SupplierSet())
            .AddSet(new LineSet()));

        var rules = server.FindSet("products")!.Rules;
        Assert.Equal([("name", "named", false), (null, "in_stock", false)], rules.Select(rule => (rule.Member?.Name, rule.Code, rule.OnlyWhenSent)));
        Assert.False(rules[1].IsValid(new Product { Stock = -1 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rules_and_validators_need_inserts_or_updates(bool validator)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(o => o
            .AddSet<Product>(b =>
            {
                b.ToSet("products").AllowWrites(WriteOperations.Delete);
                _ = validator ? b.ValidateWith<StockValidator>() : b.Must(p => p.Stock >= 0, "in_stock");
            })
            .AddSet(new CategorySet())
            .AddSet(new SupplierSet())
            .AddSet(new LineSet())));

        Assert.Contains("allows neither inserts nor updates, so they would never run", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Grouping_flags_are_exported_and_bound()
    {
        var server = Build(o => o
            .AddSet<Product>(b =>
            {
                b.ToSet("products");
                b.Property(p => p.Status).IsGroupable();
                b.Property(p => p.Stock).IsAggregatable().IsGroupable();
                b.Computed("label", p => p.Name + "!").IsGroupable();
                b.Property(p => p.Name);
            })
            .AddSet(new CategorySet())
            .AddSet(new SupplierSet())
            .AddSet(new LineSet()));

        var products = server.Model.FindSet("products")!;
        Assert.True(products.FindProperty("status") is { IsGroupable: true, IsAggregatable: false });
        Assert.True(products.FindProperty("stock") is { IsGroupable: true, IsAggregatable: true });
        Assert.True(products.FindProperty("label") is { IsGroupable: true });
        Assert.True(products.FindProperty("name") is { IsGroupable: false, IsAggregatable: false });
        Assert.Contains("\"name\": \"stock\", \"type\": \"int64\", \"groupable\": true, \"aggregatable\": true", server.Model.ToJson(), StringComparison.Ordinal);
        Assert.True(server.FindSet("products")!.FindProperty("stock") is { IsGroupable: true, IsAggregatable: true });
    }

    [Fact]
    public void Rule_arguments_are_checked_when_configured()
    {
        var builder = new EntitySetBuilder<Product>();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.Property(p => p.Name).HasMaxLength(-1));
        Assert.Throws<ArgumentException>(() => builder.Property(p => p.Name).HasPattern(""));
        Assert.Throws<ArgumentException>(() => builder.Must(p => p.Stock > 0, "Not-Snake"));
        Assert.Throws<ArgumentException>(() => builder.Must(p => p.Stock, stock => stock > 0, "required"));
        Assert.Throws<ArgumentException>(() => builder.Must(p => p.Stock > 0, new string('a', 65)));
    }

    [Fact]
    public void Navigation_target_needs_exactly_one_set()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => Build(o => o.AddSet<Category>(b => b.ToSet("categories").HasMany(c => c.Products))));
        var twice = Assert.Throws<InvalidOperationException>(() => Build(o => o
            .AddSet(new CategorySet())
            .AddSet<Product>(b => b.ToSet("products"))
            .AddSet<Product>(b => b.ToSet("cheapProducts"))));

        Assert.Contains("to Product, which has no set", missing.Message, StringComparison.Ordinal);
        Assert.Contains("to Product, which has several sets", twice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entity_outside_the_context_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(o => o.AddSet<SetConfigurationTests>(b => b.ToSet("tests"))));

        Assert.Contains("isn't an entity type of ShopContext", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_set_names_are_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(o => o
            .AddSet<Supplier>(b => b.ToSet("things"))
            .AddSet<Line>(b => b.ToSet("things"))));

        Assert.Contains("set name 'things' is used twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_from_another_context_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(o => o.AddSet<Supplier>(b => b
            .ToSet("suppliers")
            .FromSource<OtherContext>(db => db.Set<Supplier>()))));

        Assert.Contains("reads from OtherContext, but the registered DbContext is ShopContext", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_db_context_is_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddLinqToRest(o => o.AddSet(new SupplierSet())));

        Assert.Contains("UseDbContext", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Writes_need_writable_sets_and_public_setters_only_when_writable()
    {
        var readOnly = Build(o => o.AddSet<Supplier>(b => b.ToSet("suppliers").Property(s => s.Name)));
        var insertOnly = Build(o => o.AddSet<Supplier>(b => b.ToSet("suppliers").AllowWrites(WriteOperations.Insert).Property(s => s.Name)));
        var deleteOnly = Build(o => o.AddSet<Supplier>(b => b.ToSet("suppliers").AllowWrites(WriteOperations.Delete).Property(s => s.Name)));

        Assert.False(readOnly.Model.Sets[0].FindProperty("name")!.IsWritable);
        Assert.True(insertOnly.Model.Sets[0].FindProperty("name")!.IsWritable);
        Assert.False(deleteOnly.Model.Sets[0].FindProperty("name")!.IsWritable);
    }

    [Fact]
    public void Policy_must_derive_from_set_policy()
    {
        var builder = (LinqToRestServerBuilder?)null;
        new ServiceCollection().AddLinqToRest(o =>
        {
            builder = o;
            AddShop(o.UseDbContext<ShopContext>()).AddPolicy<ProductPolicy>();
        });

        Assert.Throws<ArgumentException>(() => builder!.AddPolicy<string>());
        Assert.Contains(builder!.Services, service => service.ServiceType == typeof(SetPolicy<Product>));
    }

    [Fact]
    public void Assembly_scan_finds_configurations_with_parameterless_constructors()
    {
        var builder = (LinqToRestServerBuilder?)null;
        new ServiceCollection().AddLinqToRest(o =>
        {
            builder = o;
            o.UseDbContext<ShopContext>().AddSetsFromAssembly(typeof(ProductSet).Assembly);
        });

        Assert.Equal(
            ["categories", "categories", "lines", "orderLines", "products", "products", "suppliers"],
            builder!.Sets.Select(registration => registration.Configure().Name).Order(StringComparer.Ordinal));
    }

    internal static ServerModel Build(Action<LinqToRestServerBuilder> configure)
    {
        var services = new ServiceCollection()
            .AddDbContext<ShopContext>(options => options.UseSqlite("DataSource=:memory:"))
            .AddLinqToRest(o => configure(o.UseDbContext<ShopContext>()));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ServerModel>();
    }

    private static LinqToRestServerBuilder AddShop(LinqToRestServerBuilder builder) =>
        builder.AddSet(new ProductSet()).AddSet(new CategorySet()).AddSet(new SupplierSet()).AddSet(new LineSet());

    private static void ConfigureInvalid(EntitySetBuilder<Product> b, string problem)
    {
        b.ToSet(problem == "badName" ? "Products" : "products").AllowWrites(WriteOperations.All);
        switch (problem)
        {
            case "label":
                b.Property(p => p.Label);
                break;
            case "category":
                b.Property(p => p.Category);
                break;
            case "noSetter":
                b.Property(p => p.Price);
                break;
            case "token":
                b.HasConcurrencyToken(p => p.Name);
                break;
            case "computedNavigation":
                b.Computed("category", p => p.Category);
                break;
            case "hasOneOnCollection":
                b.HasOne(p => (Category?)(object)p.Lines);
                break;
            case "unexposedForeignKey":
                b.HasOne(p => p.Category).WithForeignKey(p => p.CategoryId);
                break;
            case "wrongForeignKey":
                b.Property(p => p.SupplierId);
                b.HasOne(p => p.Category).WithForeignKey(p => p.SupplierId);
                break;
            case "noName":
                b.Definition.Name = null;
                break;
            case "badWireName":
                b.Property(p => p.Name).HasName("Title");
                break;
            case "duplicateWireName":
                b.Property(p => p.Name);
                b.Computed("name", p => p.Name);
                break;
            case "sortableCollection":
                b.Computed("lineIds", p => p.Lines.Select(l => l.Id).ToList()).IsSortable();
                break;
            case "ruleOnReadOnly":
                b.Property(p => p.Name).IsReadOnly().IsRequired();
                break;
            case "ruleOnKey":
                b.Property(p => p.Id).IsRequired();
                break;
            case "rangeOfAnotherType":
                b.Property(p => p.Stock).HasRange(1, 10);
                break;
            case "rangeOnString":
                b.Property(p => p.Name).HasRange(1, 10);
                break;
            case "minAboveMax":
                b.Property(p => p.Stock).HasRange(10L, 1L);
                break;
            case "lengthOnNumber":
                b.Property(p => p.Stock).HasMaxLength(3);
                break;
            case "patternOnNumber":
                b.Property(p => p.Stock).HasPattern("^1");
                break;
            case "nonPortablePattern":
                b.Property(p => p.Name).HasPattern("(?i)^a");
                break;
            case "groupableCollection":
                b.Computed("lineIds", p => p.Lines.Select(l => l.Id).ToList()).IsGroupable();
                break;
            case "aggregatableString":
                b.Property(p => p.Name).IsAggregatable();
                break;
            case "mustOnUnexposed":
                b.Must(p => p.Stock, stock => stock > 0, "positive");
                break;
            case "mustOnReadOnly":
                b.Property(p => p.Name).IsReadOnly();
                b.Must(p => p.Name, name => name.Length > 0, "named");
                break;
        }
    }

    public sealed class ProductPolicy : SetPolicy<Product>;

    public sealed class StockValidator : IEntityValidator<Product>
    {
        public ValueTask ValidateAsync(EntityValidationContext<Product> context, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}

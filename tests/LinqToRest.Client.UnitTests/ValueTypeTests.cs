using System.Net;
using LinqToRest.Modeling;

namespace LinqToRest.Client.UnitTests;

/// <summary>Value types on the client: binding, translation, reading, tracking and validation.</summary>
public sealed class ValueTypeTests
{
    private const string Model = """
        {
          "v": 1,
          "valueTypes": [
            {"name": "Address", "properties": [
              {"name": "street", "type": "string", "rules": {"required": true, "maxLength": 20}},
              {"name": "city", "type": "string", "sortable": true, "groupable": true},
              {"name": "geo", "type": "Geo", "nullable": true}]},
            {"name": "Geo", "properties": [{"name": "lat", "type": "float64", "aggregatable": true}, {"name": "lng", "type": "float64"}]}
          ],
          "sets": [{
            "name": "customers", "type": "Customer", "key": ["id"], "concurrencyToken": "version", "writes": ["insert", "update"],
            "pagination": {"modes": ["offset"], "defaultPageSize": 10, "maxPageSize": 10, "maxSkip": 0},
            "properties": [
              {"name": "id", "type": "int32", "sortable": true},
              {"name": "version", "type": "int32"},
              {"name": "name", "type": "string", "writable": true},
              {"name": "shippingAddress", "type": "Address", "nullable": true, "writable": true},
              {"name": "billingAddress", "type": "Address", "writable": true}
            ]
          }]
        }
        """;

    private const string Row = """
        {"id":1,"version":7,"name":"Ada","shippingAddress":{"street":"Main 1","city":"Lviv","geo":{"lat":49.8,"lng":24}},
         "billingAddress":{"street":"Side 2","city":"Kyiv","geo":null}}
        """;

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    private static readonly ValueBinding<Geo> GeoBinding = new ValueBinding<Geo>("Geo", static v => new Geo { Lat = (double)v[0]!, Lng = (double)v[1]! })
        .Property("lat", nameof(Geo.Lat), static g => g.Lat)
        .Property("lng", nameof(Geo.Lng), static g => g.Lng);

    private static readonly ValueBinding<Address> AddressBinding = new ValueBinding<Address>("Address", static v => new Address { Street = (string)v[0]!, City = (string)v[1]!, Geo = (Geo?)v[2] })
        .Property("street", nameof(Address.Street), static a => a.Street)
        .Property("city", nameof(Address.City), static a => a.City)
        .Value("geo", nameof(Address.Geo), GeoBinding, static a => a.Geo);

    [Fact]
    public void Binds_value_types_through_delegates()
    {
        var model = ClientModel.Bind(RestModel.Parse(Model), CustomerBinding(AddressBinding));

        var shipping = model.FindSet(typeof(Customer))!.FindProperty("shippingAddress")!;
        Assert.Equal("Address", shipping.ValueType!.Name);
        Assert.Equal(["Street", "City", "Geo"], shipping.ValueType.Properties.Select(property => property.Name));
        Assert.Same(shipping.ValueType, model.FindSet(typeof(Customer))!.FindProperty("billingAddress")!.ValueType);
        Assert.Equal("Geo", shipping.ValueType.FindProperty("geo")!.ValueType!.Name);
    }

    [Fact]
    public void Rejects_value_bindings_that_do_not_match_the_model()
    {
        var wrongName = new ValueBinding<Address>("Place", static _ => null!).Property("street", "Street", static a => a.Street);
        var missing = new ValueBinding<Address>("Address", static _ => null!).Property("street", "Street", static a => a.Street).Property("city", "City", static a => a.City);
        var extra = new ValueBinding<Address>("Address", static _ => null!)
            .Property("street", "Street", static a => a.Street).Property("city", "City", static a => a.City).Value("geo", "Geo", GeoBinding, static a => a.Geo)
            .Property("zip", "Street", static a => a.Street);
        var mistyped = new ValueBinding<Address>("Address", static _ => null!)
            .Property("street", "Street", static a => a.Street.Length).Property("city", "City", static a => a.City).Value("geo", "Geo", GeoBinding, static a => a.Geo);

        Assert.Contains("binds 'ShippingAddress' to the value type 'Place', but the model says Address", Bind(wrongName), StringComparison.Ordinal);
        Assert.Contains("Address, the class of the value type 'Address', has no binding for 'geo'", Bind(missing), StringComparison.Ordinal);
        Assert.Contains("binds 'zip', which the model doesn't have", Bind(extra), StringComparison.Ordinal);
        Assert.Contains("has 'Street' of type Int32, but the model says string", Bind(mistyped), StringComparison.Ordinal);

        static string Bind(ValueBinding<Address> address) =>
            Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(RestModel.Parse(Model), CustomerBinding(address))).Message;
    }

    [Fact]
    public async Task Value_members_translate_to_paths_and_whole_values_to_null_tests()
    {
        var (context, http) = Create();

        await context.Customers
            .Where(c => c.ShippingAddress != null && c.ShippingAddress.City == "Lviv" && c.BillingAddress.Geo!.Lat > 40)
            .OrderBy(c => c.BillingAddress.City)
            .Take(10)
            .ToListAsync(Token);

        var body = http.LastJson;
        Assert.Equal(
            """{"and":[{"ne":[{"member":"shippingAddress"},null]},{"eq":[{"member":"shippingAddress/city"},"Lviv"]},{"gt":[{"member":"billingAddress/geo/lat"},40]}]}""",
            body.GetProperty("filter").GetRawText());
        Assert.Equal("""[{"member":"billingAddress/city"}]""", body.GetProperty("orderBy").GetRawText());
    }

    [Fact]
    public async Task Whole_values_are_refused_locally_outside_selects_and_null_tests()
    {
        var (context, http) = Create();
        var lviv = new Address { Street = "Main 1", City = "Lviv" };

        var compared = await Assert.ThrowsAsync<NotSupportedException>(() => context.Customers.Where(c => c.ShippingAddress == lviv).Take(10).ToListAsync(Token));
        var sorted = await Assert.ThrowsAsync<NotSupportedException>(() => context.Customers.OrderBy(c => c.BillingAddress).Take(10).ToListAsync(Token));
        var used = await Assert.ThrowsAsync<NotSupportedException>(() => context.Customers.Where(c => c.BillingAddress.Equals(lviv)).Take(10).ToListAsync(Token));

        Assert.Contains("can only be selected or compared with null", compared.Message, StringComparison.Ordinal);
        Assert.Contains("Sort by a scalar entity member", sorted.Message, StringComparison.Ordinal);
        Assert.Contains("Address.Equals can't be translated", used.Message, StringComparison.Ordinal);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Rows_and_projections_read_values()
    {
        var (context, http) = Create();
        http.Respond($$"""{"items":[{{Row}}]}""");
        http.Respond("""{"items":[{"name":"Ada","shippingAddress":null,"city":"Kyiv","geo":{"lat":1,"lng":2}}]}""");

        var customer = Assert.Single(await context.Customers.Take(10).ToListAsync(Token));
        var projected = Assert.Single(await context.Customers
            .Select(c => new { c.Name, c.ShippingAddress, c.BillingAddress.City, c.BillingAddress.Geo })
            .Take(10)
            .ToListAsync(Token));

        Assert.Equal(new Address { Street = "Main 1", City = "Lviv", Geo = new Geo { Lat = 49.8, Lng = 24 } }, customer.ShippingAddress);
        Assert.Null(customer.BillingAddress.Geo);
        Assert.Equal(("Ada", null, "Kyiv", new Geo { Lat = 1, Lng = 2 }), (projected.Name, projected.ShippingAddress, projected.City, projected.Geo));
        Assert.Equal(
            """{"new":{"name":{"member":"name"},"shippingAddress":{"member":"shippingAddress"},"city":{"member":"billingAddress/city"},"geo":{"member":"billingAddress/geo"}}}""",
            http.LastJson.GetProperty("select").GetRawText());
    }

    [Fact]
    public async Task A_replaced_value_is_sent_whole_and_an_equal_one_is_not_a_change()
    {
        var (context, http) = Create();
        http.Respond($$"""{"items":[{{Row}}]}""");
        var customer = Assert.Single(await context.Customers.Take(10).ToListAsync(Token));

        customer.BillingAddress = customer.BillingAddress with { };
        Assert.Equal(EntityState.Unchanged, context.Entry(customer).State);
        customer.ShippingAddress = customer.ShippingAddress! with { City = "Lviv-2" };
        http.Respond($$"""{"results":[{"id":"o0","value":{{Row.Replace("\"Lviv\"", "\"Lviv-2\"", StringComparison.Ordinal)}}}]}""");
        await context.SaveChangesAsync(Token);

        Assert.Equal(
            """{"shippingAddress":{"street":"Main 1","city":"Lviv-2","geo":{"lat":49.8,"lng":24}}}""",
            http.LastJson.GetProperty("operations")[0].GetProperty("values").GetRawText());
        Assert.Equal(EntityState.Unchanged, context.Entry(customer).State);
    }

    [Fact]
    public async Task Inserts_send_null_and_nested_values()
    {
        var (context, http) = Create();
        context.Customers.Add(new Customer { Name = "Bo", BillingAddress = new Address { Street = "S", City = "C", Geo = new Geo { Lat = 1, Lng = 2 } } });
        http.Respond("""{"results":[]}""");

        await context.SaveChangesAsync(Token);

        Assert.Equal(
            """{"name":"Bo","shippingAddress":null,"billingAddress":{"street":"S","city":"C","geo":{"lat":1,"lng":2}}}""",
            http.LastJson.GetProperty("operations")[0].GetProperty("values").GetRawText());
    }

    [Fact]
    public async Task Mutable_hand_written_values_are_tracked_by_their_members()
    {
        var model = ClientModel.Bind(RestModel.Parse(Model), new Dictionary<string, Type> { ["customers"] = typeof(PlainCustomer) });
        var http = new StubHandler();
        var context = new PlainContext(new RestDataContextOptions<PlainContext>(model, new HttpClient(http) { BaseAddress = new Uri("https://shop.test/api/") }));
        http.Respond($$"""{"items":[{{Row}}]}""");
        var customer = Assert.Single(await context.Customers.Take(10).ToListAsync(Token));

        customer.BillingAddress.City = "Odesa";
        http.Respond("""{"results":[]}""");
        await context.SaveChangesAsync(Token);

        Assert.Equal(("Main 1", 49.8), (customer.ShippingAddress!.Street, customer.ShippingAddress.Geo!.Lat));
        Assert.Equal(
            """{"billingAddress":{"street":"Side 2","city":"Odesa","geo":null}}""",
            http.LastJson.GetProperty("operations")[0].GetProperty("values").GetRawText());
    }

    [Fact]
    public async Task Value_members_are_validated_locally_by_dotted_path()
    {
        var (context, http) = Create();
        var customer = new Customer { Name = "Bo", ShippingAddress = new Address { Street = "A street far too long to fit", City = "C" }, BillingAddress = null! };
        context.Customers.Add(customer);

        var error = await Assert.ThrowsAsync<RestValidationException>(() => context.SaveChangesAsync(Token));

        Assert.True(error.IsLocal);
        Assert.Equal([("ShippingAddress.Street", "too_long"), ("BillingAddress", "required")], error.Errors.Select(e => (e.Member, e.Code)));
        Assert.Equal(["ShippingAddress.Street", "BillingAddress"], error.ErrorsFor(customer).Keys);
        Assert.Empty(http.Requests);
        Assert.Equal(20, context.Customers.Rules(c => c.ShippingAddress!.Street)!.MaxLength);
    }

    [Fact]
    public async Task Server_errors_on_value_members_map_to_dotted_paths()
    {
        var (context, http) = Create();
        var customer = new Customer { Name = "Bo", BillingAddress = new Address { Street = "S", City = "C" } };
        context.Customers.Add(customer);
        http.Problem((HttpStatusCode)422, """
            {"type":"urn:linqtorest:error:validation_failed","title":"Validation failed.","status":422,"code":"validation_failed","pointer":"",
             "errors":[{"operation":"o0","pointer":"/operations/0/values/billingAddress/geo/lat","member":"billingAddress/geo/lat","code":"out_of_range"}]}
            """);

        var error = await Assert.ThrowsAsync<RestValidationException>(() => context.SaveChangesAsync(Token));

        Assert.Equal(("BillingAddress.Geo.Lat", "out_of_range"), (error.Errors[0].Member, error.Errors[0].Code));
    }

    private static (CustomersContext Context, StubHandler Http) Create()
    {
        var model = ClientModel.Bind(RestModel.Parse(Model), CustomerBinding(AddressBinding));
        var http = new StubHandler();
        var client = new HttpClient(http) { BaseAddress = new Uri("https://shop.test/api/") };
        return (new CustomersContext(new RestDataContextOptions<CustomersContext>(model, client)), http);
    }

    private static SetBinding<Customer> CustomerBinding(ValueBinding<Address> address) =>
        new SetBinding<Customer>("customers", static () => new Customer())
            .Property("id", nameof(Customer.Id), static c => c.Id, static (c, v) => c.Id = v)
            .Property("version", nameof(Customer.Version), static c => c.Version, static (c, v) => c.Version = v)
            .Property("name", nameof(Customer.Name), static c => c.Name, static (c, v) => c.Name = v)
            .Value("shippingAddress", nameof(Customer.ShippingAddress), address, static c => c.ShippingAddress, static (c, v) => c.ShippingAddress = v)
            .Value("billingAddress", nameof(Customer.BillingAddress), address, static c => c.BillingAddress, static (c, v) => c.BillingAddress = v!);

    public sealed class CustomersContext(RestDataContextOptions<CustomersContext> options) : RestDataContext(options)
    {
        public RestSet<Customer> Customers => Set<Customer>();
    }

    public sealed class PlainContext(RestDataContextOptions<PlainContext> options) : RestDataContext(options)
    {
        public RestSet<PlainCustomer> Customers => Set<PlainCustomer>();
    }

    public sealed class Customer
    {
        public int Id { get; set; }
        public int Version { get; set; }
        public string Name { get; set; } = "";
        public Address? ShippingAddress { get; set; }
        public Address BillingAddress { get; set; } = new() { Street = "", City = "" };
    }

    public sealed record Address
    {
        public required string Street { get; init; }
        public required string City { get; init; }
        public Geo? Geo { get; init; }
    }

    public sealed record Geo
    {
        public required double Lat { get; init; }
        public required double Lng { get; init; }
    }
}

/// <summary>A hand-written class with a mutable value class and a positional record, bound by reflection.</summary>
public sealed class PlainCustomer
{
    public int Id { get; set; }
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public PlainAddress? ShippingAddress { get; set; }
    public PlainAddress BillingAddress { get; set; } = new();
}

public sealed class PlainAddress
{
    public string Street { get; set; } = "";
    public string City { get; set; } = "";
    public PlainGeo? Geo { get; set; }
}

public sealed record PlainGeo(double Lat, double Lng);

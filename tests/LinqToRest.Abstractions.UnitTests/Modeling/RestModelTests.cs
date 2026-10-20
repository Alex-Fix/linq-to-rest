using System.Text;
using System.Text.Json.Nodes;
using LinqToRest.Modeling;

namespace LinqToRest.Abstractions.UnitTests.Modeling;

public sealed class RestModelTests
{
    private const string DesignExample = """
        {
          "v": 1,
          "enums": {"ProductStatus": ["Draft", "Active", "Retired"]},
          "sets": [{
            "name": "products", "type": "Product",
            "key": ["id"], "concurrencyToken": "version",
            "writes": ["insert", "update"],
            "pagination": {"modes": ["offset", "cursor"], "defaultPageSize": 50, "maxPageSize": 200, "maxSkip": 10000},
            "properties": [
              {"name": "id", "type": "int32", "sortable": true},
              {"name": "name", "type": "string", "writable": true},
              {"name": "price", "type": "decimal", "writable": true, "sortable": true},
              {"name": "status", "type": "ProductStatus", "writable": true},
              {"name": "version", "type": "guid"},
              {"name": "categoryId", "type": "int32", "nullable": true, "writable": true},
              {"name": "categoryName", "type": "string", "nullable": true},
              {"name": "tags", "type": "string", "collection": true},
              {"name": "description", "type": "string", "nullable": true, "writable": true, "filterable": false}
            ],
            "navigations": [{"name": "category", "target": "categories", "nullable": true, "foreignKey": ["categoryId"]}]
          }, {
            "name": "categories", "type": "Category",
            "key": ["id"],
            "writes": [],
            "pagination": {"modes": ["offset"], "defaultPageSize": 20, "maxPageSize": 100, "maxSkip": 0},
            "properties": [
              {"name": "id", "type": "int32", "sortable": true},
              {"name": "name", "type": "string"}
            ],
            "navigations": [{"name": "products", "target": "products", "collection": true}]
          }]
        }
        """;

    [Fact]
    public void Reads_the_design_example()
    {
        var model = RestModel.Parse(DesignExample);

        var products = model.FindSet("products")!;
        Assert.Equal("Product", products.TypeName);
        Assert.Equal(["id"], products.Key);
        Assert.Equal("version", products.ConcurrencyToken);
        Assert.Equal(WriteOperations.Insert | WriteOperations.Update, products.Writes);
        Assert.True(products.Allows(WriteOperations.Update));
        Assert.False(products.Allows(WriteOperations.Update | WriteOperations.Delete));
        Assert.Equal(PaginationModes.All, products.Pagination.Modes);
        Assert.Equal("ProductStatus", products.FindProperty("status")!.Type);
        Assert.True(products.FindProperty("tags")!.IsCollection);
        Assert.False(products.FindProperty("description")!.IsFilterable);
        Assert.True(products.FindProperty("name")!.IsFilterable);
        Assert.Equal(["categoryId"], products.FindNavigation("category")!.ForeignKey);
        Assert.Equal(WriteOperations.None, model.FindSet("categories")!.Writes);
        Assert.True(model.FindSet("categories")!.FindNavigation("products")!.IsCollection);
        Assert.Equal(["Draft", "Active", "Retired"], model.FindEnum("ProductStatus")!.Members);
        Assert.Null(model.FindSet("orders"));
    }

    [Fact]
    public void Write_then_read_round_trips()
    {
        var written = RestModel.Parse(DesignExample).ToJson();

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(DesignExample), JsonNode.Parse(written)));
        Assert.Equal(written, RestModel.Parse(written).ToJson());
    }

    [Fact]
    public void Writes_false_flags_and_empty_lists_compactly()
    {
        var model = new RestModel([Set("products", "Product", [Key("id")])]);

        Assert.Equal(
            """{"v":1,"sets":[{"name":"products","type":"Product","key":["id"],"writes":[],"pagination":{"modes":["offset","cursor"],"defaultPageSize":50,"maxPageSize":200,"maxSkip":10000},"properties":[{"name":"id","type":"int32","sortable":true}],"navigations":[]}]}""",
            model.ToJson(indented: false));
    }

    [Fact]
    public void Indented_output_uses_two_spaces()
    {
        var json = new RestModel([Set("products", "Product", [Key("id")])]).ToJson();

        Assert.StartsWith("{\n  \"v\": 1,\n  \"sets\": [", json.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"v":1,"sets":[],"extra":1}""", "unknown key 'extra'")]
    [InlineData("""{"v":2,"sets":[]}""", "version 2")]
    [InlineData("""{"sets":[]}""", "'/v' must be a number")]
    [InlineData("""{"v":1}""", "missing 'sets'")]
    [InlineData("""{"v":1,"sets":[]}""", "no sets")]
    [InlineData("""{"v":1,"v":1,"sets":[]}""", "twice")]
    [InlineData("""[1]""", "must be an object")]
    [InlineData("""{"v":1,"sets":[""", "isn't valid JSON")]
    public void Rejects_invalid_documents(string json, string message)
    {
        var exception = Assert.Throws<FormatException>(() => RestModel.Parse(json));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/sets/0/extra", "1", "unknown key 'extra'")]
    [InlineData("/sets/0/properties/0/extra", "1", "unknown key 'extra'")]
    [InlineData("/sets/0/navigations/0/extra", "1", "unknown key 'extra'")]
    [InlineData("/sets/0/pagination/extra", "1", "unknown key 'extra'")]
    [InlineData("/sets/0/properties/1/type", "\"money\"", "unknown type 'money'")]
    [InlineData("/sets/0/properties/1/writable", "1", "must be true or false")]
    [InlineData("/sets/0/writes", "[\"upsert\"]", "unknown operation 'upsert'")]
    [InlineData("/sets/0/writes", "[\"insert\",\"insert\"]", "'insert' twice")]
    [InlineData("/sets/0/writes", "[]", "is writable, but the set allows neither")]
    [InlineData("/sets/0/pagination/modes", "[\"page\"]", "unknown mode 'page'")]
    [InlineData("/sets/0/pagination/modes", "[]", "at least one valid pagination mode")]
    [InlineData("/sets/0/pagination/defaultPageSize", "500", "defaultPageSize <= maxPageSize")]
    [InlineData("/sets/0/pagination/maxSkip", "-1", "maxSkip >= 0")]
    [InlineData("/sets/0/pagination/maxSkip", "1.5", "32-bit integer")]
    [InlineData("/sets/0/name", "\"Products\"", "set name 'Products' is invalid")]
    [InlineData("/sets/1/name", "\"products\"", "set name 'products' is used twice")]
    [InlineData("/sets/0/type", "\"product\"", "invalid type name 'product'")]
    [InlineData("/sets/0/type", "\"Status\"", "type name 'Status' is used twice")]
    [InlineData("/sets/0/properties/1/name", "\"id\"", "duplicate member name 'id'")]
    [InlineData("/sets/0/properties/1/name", "\"Name\"", "invalid or duplicate member name 'Name'")]
    [InlineData("/sets/0/navigations/0/name", "\"name\"", "duplicate member name 'name'")]
    [InlineData("/sets/0/key", "[]", "has no key")]
    [InlineData("/sets/0/key", "[\"id\",\"id\"]", "key property twice")]
    [InlineData("/sets/0/key", "[\"name\"]", "key 'products.name'")]
    [InlineData("/sets/0/key", "[\"missing\"]", "key 'products.missing'")]
    [InlineData("/sets/0/concurrencyToken", "\"id\"", "concurrency token 'products.id'")]
    [InlineData("/sets/0/concurrencyToken", "\"name\"", "concurrency token 'products.name'")]
    [InlineData("/sets/0/properties/3/collection", "true", "collection of binary values")]
    [InlineData("/sets/0/properties/2/sortable", "true", "collections and binary values can't be sorted")]
    [InlineData("/sets/0/navigations/0/target", "\"orders\"", "unknown set 'orders'")]
    [InlineData("/sets/0/navigations/0/foreignKey", "[\"name\"]", "foreign key that doesn't match")]
    [InlineData("/sets/0/navigations/0/foreignKey", "[\"categoryId\",\"id\"]", "foreign key that doesn't match")]
    [InlineData("/sets/0/navigations/0/collection", "true", "can't be nullable or have a foreign key")]
    [InlineData("/enums/Status", "[]", "has no members")]
    [InlineData("/enums/Status", "[\"A\",\"A\"]", "duplicate member 'A'")]
    [InlineData("/enums/Status", "[\"1A\"]", "invalid or duplicate member '1A'")]
    [InlineData("/sets/0/properties/2/type", "\"string\"", "enum 'Status' isn't used")]
    public void Rejects_inconsistent_models(string location, string value, string message)
    {
        var json = Minimal();
        Set(json, location, JsonNode.Parse(value));

        var exception = Assert.Throws<FormatException>(() => RestModel.Parse(json.ToJsonString()));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Minimal_document_is_valid()
    {
        var model = RestModel.Parse(Minimal().ToJsonString());

        Assert.Equal(2, model.Sets.Count);
    }

    [Fact]
    public void Document_size_is_limited()
    {
        var json = Minimal().ToJsonString();
        var padded = json[..^1] + new string(' ', RestModelJson.MaxBytes - json.Length) + "}";

        Assert.Equal(RestModelJson.MaxBytes, Encoding.UTF8.GetByteCount(padded));
        RestModel.Parse(padded);
        var exception = Assert.Throws<FormatException>(() => RestModel.Parse(padded + " "));
        Assert.Contains("larger than", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Set_count_is_limited()
    {
        static string Sets(int count) => new JsonObject
        {
            ["v"] = 1,
            ["sets"] = new JsonArray([.. Enumerable.Range(0, count).Select(index => JsonNode.Parse($$"""
                {"name":"s{{index}}","type":"S{{index}}","key":["id"],
                 "pagination":{"modes":["offset"],"defaultPageSize":1,"maxPageSize":1,"maxSkip":0},
                 "properties":[{"name":"id","type":"int32","sortable":true}]}
                """))]),
        }.ToJsonString();

        Assert.Equal(RestModelJson.MaxSets, RestModel.Parse(Sets(RestModelJson.MaxSets)).Sets.Count);
        var exception = Assert.Throws<FormatException>(() => RestModel.Parse(Sets(RestModelJson.MaxSets + 1)));
        Assert.Contains("more than 1000 entries", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Member_count_is_limited()
    {
        static string Members(int count)
        {
            var json = Minimal();
            json["sets"]![1]!["properties"] = new JsonArray([
                new JsonObject { ["name"] = "id", ["type"] = "int32", ["sortable"] = true },
                .. Enumerable.Range(1, count - 1).Select(index => new JsonObject { ["name"] = $"p{index}", ["type"] = "string" }),
            ]);
            return json.ToJsonString();
        }

        Assert.Equal(RestModelJson.MaxMembers, RestModel.Parse(Members(RestModelJson.MaxMembers)).FindSet("categories")!.Properties.Count);
        Assert.Throws<FormatException>(() => RestModel.Parse(Members(RestModelJson.MaxMembers + 1)));
    }

    [Fact]
    public void Depth_is_limited()
    {
        var json = Minimal();
        json["sets"]![0]!["properties"]![1]!["type"] = JsonNode.Parse("[[[[[[[[1]]]]]]]]");

        var exception = Assert.Throws<FormatException>(() => RestModel.Parse(json.ToJsonString()));

        Assert.Contains("isn't valid JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_validates_too()
    {
        var exception = Assert.Throws<ArgumentException>(() => new RestModel([Set("products", "Product", [new RestPropertyModel { Name = "id", Type = "int32", IsSortable = true, IsNullable = true }])]));

        Assert.Contains("key 'products.id'", exception.Message, StringComparison.Ordinal);
    }

    private static JsonObject Minimal() => JsonNode.Parse("""
        {
          "v": 1,
          "enums": {"Status": ["Active", "Retired"]},
          "sets": [{
            "name": "products", "type": "Product", "key": ["id"], "concurrencyToken": "version",
            "writes": ["insert", "update", "delete"],
            "pagination": {"modes": ["offset", "cursor"], "defaultPageSize": 50, "maxPageSize": 200, "maxSkip": 10000},
            "properties": [
              {"name": "id", "type": "int32", "sortable": true},
              {"name": "name", "type": "string", "writable": true},
              {"name": "status", "type": "Status", "writable": true, "collection": true},
              {"name": "version", "type": "bytes"},
              {"name": "categoryId", "type": "int32", "nullable": true, "writable": true}
            ],
            "navigations": [{"name": "category", "target": "categories", "nullable": true, "foreignKey": ["categoryId"]}]
          }, {
            "name": "categories", "type": "Category", "key": ["id"],
            "pagination": {"modes": ["offset"], "defaultPageSize": 50, "maxPageSize": 200, "maxSkip": 10000},
            "properties": [{"name": "id", "type": "int32", "sortable": true}]
          }]
        }
        """)!.AsObject();

    private static void Set(JsonObject root, string location, JsonNode? value)
    {
        var segments = location.Split('/')[1..];
        JsonNode node = root;
        foreach (var segment in segments[..^1])
        {
            node = int.TryParse(segment, out var index) ? node[index]! : node[segment]!;
        }

        if (int.TryParse(segments[^1], out var last))
        {
            node[last] = value;
        }
        else
        {
            node[segments[^1]] = value;
        }
    }

    private static RestPropertyModel Key(string name) => new() { Name = name, Type = "int32", IsSortable = true };

    private static RestSetModel Set(string name, string typeName, IReadOnlyList<RestPropertyModel> properties) => new()
    {
        Name = name,
        TypeName = typeName,
        Key = ["id"],
        Pagination = new RestPaginationModel(),
        Properties = properties,
    };
}

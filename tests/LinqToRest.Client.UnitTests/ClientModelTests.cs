using LinqToRest.Modeling;

namespace LinqToRest.Client.UnitTests;

public sealed class ClientModelTests
{
    private static readonly Dictionary<string, Type> Types = new() { ["notes"] = typeof(Note), ["authors"] = typeof(Author) };

    [Fact]
    public void Binds_members_by_camel_case_name_and_ignores_extra_properties()
    {
        var model = ClientModel.Bind(Document(), Types);

        var notes = model.FindSet(typeof(Note))!;
        Assert.Equal(["Id", "Text", "Kind", "Score", "Labels", "AuthorId"], notes.Properties.Select(property => property.Name));
        Assert.True(notes.Properties[0].IsKey);
        Assert.Same(model.FindSet(typeof(Author)), notes.Navigations.Single().Target);
        Assert.Same(notes.Properties[^1], notes.Navigations.Single().ForeignKey);
        Assert.Null(notes.FindProperty("local"));
    }

    [Theory]
    [InlineData("""{"name": "text", "type": "string",""", """{"name": "text", "type": "int32",""", "has 'Text' of type String, but the model says int32")]
    [InlineData("""{"name": "text", "type": "string",""", """{"name": "text", "type": "string", "collection": true,""", "has 'Text' as a single value, but the model says otherwise")]
    [InlineData("""{"name": "labels", "type": "string", "collection": true}""", """{"name": "labels", "type": "string"}""", "has 'Labels' as a collection, but the model says otherwise")]
    [InlineData("""{"name": "score", "type": "int32"}""", """{"name": "score", "type": "int32", "nullable": true}""", "has 'Score' as non-nullable, but the model says it can be null")]
    [InlineData("""{"name": "score", "type": "int32"}""", """{"name": "score", "type": "int32"}, {"name": "missing", "type": "string"}""", "has no public read-write property for 'missing'")]
    [InlineData("""{"name": "score", "type": "int32"}""", """{"name": "score", "type": "int32"}, {"name": "computed", "type": "string"}""", "has no public read-write property for 'computed'")]
    [InlineData("""{"name": "kind", "type": "Kind"}""", """{"name": "kind", "type": "Other"}""", "has 'Kind' of type Kind, but the model says Other")]
    public void Rejects_a_class_that_does_not_match(string find, string replace, string message)
    {
        var json = Template().Replace(find, replace, StringComparison.Ordinal);
        if (replace.Contains("Other", StringComparison.Ordinal))
        {
            json = json.Replace("\"Kind\": [", "\"Other\": [", StringComparison.Ordinal);
        }

        var exception = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(RestModel.Parse(json), Types));

        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_enum_members_that_differ()
    {
        var json = Template().Replace("[\"Plain\", \"Rich\"]", "[\"Plain\", \"Html\"]", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(RestModel.Parse(json), Types));

        Assert.Contains("has 'Kind' of type Kind, but the model says Kind", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_navigation_of_another_type()
    {
        var types = new Dictionary<string, Type> { ["notes"] = typeof(Note), ["authors"] = typeof(Writer) };

        var exception = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), types));

        Assert.Contains("has the navigation 'Author' of type Author, but the model says Writer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Needs_exactly_one_class_per_set()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), new Dictionary<string, Type> { ["notes"] = typeof(Note) }));
        var unknown = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), new Dictionary<string, Type>(Types) { ["tags"] = typeof(Note) }));
        var twice = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), new Dictionary<string, Type> { ["notes"] = typeof(Note), ["authors"] = typeof(Note) }));

        Assert.Contains("No class is given for the set 'authors'", missing.Message, StringComparison.Ordinal);
        Assert.Contains("The model has no set 'tags'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("is bound to another set too", twice.Message, StringComparison.Ordinal);
    }

    internal static RestModel Document(string writes = "\"insert\", \"update\", \"delete\"") => RestModel.Parse(Template(writes));

    private static string Template(string writes = "\"insert\", \"update\", \"delete\"") => $$"""
        {
          "v": 1,
          "enums": {"Kind": ["Plain", "Rich"]},
          "sets": [
            {
              "name": "notes", "type": "Note", "key": ["id"], "writes": [{{writes}}],
              "pagination": {"modes": ["offset"], "defaultPageSize": 10, "maxPageSize": 10, "maxSkip": 0},
              "properties": [
                {"name": "id", "type": "int32", "sortable": true},
                {"name": "text", "type": "string", "writable": {{(writes.Length > 0 ? "true" : "false")}}},
                {"name": "kind", "type": "Kind"},
                {"name": "score", "type": "int32"},
                {"name": "labels", "type": "string", "collection": true},
                {"name": "authorId", "type": "int32", "nullable": true}
              ],
              "navigations": [{"name": "author", "target": "authors", "nullable": true, "foreignKey": ["authorId"]}]
            },
            {
              "name": "authors", "type": "Author", "key": ["id"],
              "pagination": {"modes": ["offset"], "defaultPageSize": 10, "maxPageSize": 10, "maxSkip": 0},
              "properties": [{"name": "id", "type": "int32", "sortable": true}]
            }
          ]
        }
        """;

    public enum Kind
    {
        Plain,
        Rich,
    }

    public sealed class Note
    {
        public int Id { get; set; }
        public string Text { get; set; } = "";
        public Kind Kind { get; set; }
        public int Score { get; set; }
        public List<string> Labels { get; set; } = [];
        public int? AuthorId { get; set; }
        public Author? Author { get; set; }
        public string Local { get; set; } = "";
        public string Computed => Text;
    }

    public sealed class Author
    {
        public int Id { get; set; }
    }

    public sealed class Writer
    {
        public int Id { get; set; }
    }
}

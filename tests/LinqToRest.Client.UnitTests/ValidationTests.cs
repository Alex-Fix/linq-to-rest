using System.Net;
using LinqToRest.Modeling;
using static LinqToRest.Client.UnitTests.ClientModelTests;

namespace LinqToRest.Client.UnitTests;

/// <summary>The model's built-in rules on the client, and the server's validation problems mapped to entities.</summary>
public sealed class ValidationTests
{
    private const string Model = """
        {
          "v": 1,
          "enums": {"Kind": ["Plain", "Rich"]},
          "sets": [
            {
              "name": "notes", "type": "Note", "key": ["id"], "writes": ["insert", "update"],
              "pagination": {"modes": ["offset"], "defaultPageSize": 10, "maxPageSize": 10, "maxSkip": 0},
              "properties": [
                {"name": "id", "type": "int32", "sortable": true},
                {"name": "text", "type": "string", "writable": true, "rules": {"required": true, "maxLength": 5}},
                {"name": "kind", "type": "Kind"},
                {"name": "score", "type": "int32", "writable": true, "rules": {"min": 0, "max": 10}},
                {"name": "labels", "type": "string", "collection": true, "writable": true, "rules": {"maxLength": 2}},
                {"name": "authorId", "type": "int32", "nullable": true, "writable": true, "rules": {"required": true}}
              ],
              "navigations": [{"name": "author", "target": "authors", "nullable": true, "foreignKey": ["authorId"]}]
            },
            {
              "name": "authors", "type": "Author", "key": ["id"], "writes": ["insert"],
              "pagination": {"modes": ["offset"], "defaultPageSize": 10, "maxPageSize": 10, "maxSkip": 0},
              "properties": [{"name": "id", "type": "int32", "sortable": true}]
            }
          ]
        }
        """;

    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Save_checks_the_built_in_rules_before_sending()
    {
        var (context, http) = Create();
        var note = new Note { Text = " ", Score = 11, Labels = ["a", "b", "c"], AuthorId = 1 };
        context.Notes.Add(note);

        var error = await Assert.ThrowsAsync<RestValidationException>(() => context.SaveChangesAsync(Token));

        Assert.True(error.IsLocal);
        Assert.Equal((422, "validation_failed"), (error.Status, error.Code));
        Assert.Equal([(note, "Text", "required"), (note, "Score", "out_of_range"), (note, "Labels", "too_long")], error.Errors.Select(e => (e.Entity, e.Member, e.Code)));
        Assert.Equal([note], error.Entities);
        Assert.Empty(http.Requests);
        Assert.Equal(EntityState.Added, context.Entry(note).State);
    }

    [Fact]
    public async Task Updates_check_only_the_members_they_send()
    {
        var (context, http) = Create();
        var note = new Note { Id = 1, Text = "too long already", Score = 1, AuthorId = 1 };
        context.Notes.Attach(note);
        http.Respond("""{"results":[{"id":"o0"}]}""");

        note.Score = 2;
        await context.SaveChangesAsync(Token);
        note.Score = -1;
        var error = await Assert.ThrowsAsync<RestValidationException>(() => context.SaveChangesAsync(Token));

        Assert.Single(http.Requests);
        Assert.Equal([("Score", "out_of_range")], error.Errors.Select(e => (e.Member, e.Code)));
    }

    [Fact]
    public async Task A_foreign_key_set_through_a_new_row_is_not_checked_yet()
    {
        var (context, http) = Create();
        var author = new Author();
        var note = new Note { Text = "Hi", Author = author };
        context.Set<Author>().Add(author);
        context.Notes.Add(note);
        http.Respond("""{"results":[{"id":"o0"},{"id":"o1"}]}""");

        Assert.Equal(2, await context.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task Server_validation_errors_map_to_entities_and_members()
    {
        // The golden shop and the response contract shared with the TypeScript client's tests.
        var (shop, http) = Shop.Create();
        var first = new ProductDto { Name = "One" };
        var second = new ProductDto { Name = "Two" };
        shop.Products.Add(first);
        shop.Products.Add(second);
        http.Problem(HttpStatusCode.UnprocessableEntity, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "golden", "response-validation-failed.json"), Token));

        var error = await Assert.ThrowsAsync<RestValidationException>(() => shop.SaveChangesAsync(Token));

        Assert.False(error.IsLocal);
        Assert.True(error.IsTruncated);
        Assert.Equal([(first, "Name", "duplicate"), (second, null, "too_many_products")], error.Errors.Select(e => (e.Entity, e.Member, e.Code)));
        Assert.Equal(["duplicate"], error.ErrorsFor(first)["Name"]);
        Assert.Equal(["too_many_products"], error.ErrorsFor(second)[""]);
        Assert.Equal(EntityState.Added, shop.Entry(first).State);
    }

    [Fact]
    public async Task Other_save_errors_name_their_member()
    {
        var (context, http) = Create();
        context.Notes.Add(new Note { Text = "Hi", AuthorId = 3 });
        http.Problem(HttpStatusCode.NotFound, """{"type":"urn:linqtorest:error:not_found","title":"Not found.","status":404,"code":"not_found","pointer":"/operations/0/values/authorId","operationId":"o0"}""");

        var error = await Assert.ThrowsAsync<RestSaveException>(() => context.SaveChangesAsync(Token));

        Assert.Equal(("not_found", "AuthorId"), (error.Code, error.Member));
    }

    [Fact]
    public void Forms_can_check_an_entity_and_read_the_rules()
    {
        var (context, _) = Create();

        var errors = context.Validate(new Note { Text = "Too long", Score = 5 });

        Assert.Equal([("Text", "too_long"), ("AuthorId", "required")], errors.Select(e => (e.Member, e.Code)));
        Assert.True(context.Notes.Rules(n => n.Text) is { IsRequired: true, MaxLength: 5 });
        Assert.Equal((0, 10), (context.Notes.Rules(n => n.Score)!.Min, context.Notes.Rules(n => n.Score)!.Max));
        Assert.Null(context.Notes.Rules(n => n.Kind));
        Assert.Throws<ArgumentException>(() => context.Notes.Rules(n => n.Local));
    }

    private static (NotesContext Context, StubHandler Http) Create()
    {
        var model = ClientModel.Bind(RestModel.Parse(Model), new Dictionary<string, Type> { ["notes"] = typeof(Note), ["authors"] = typeof(Author) });
        var http = new StubHandler();
        var client = new HttpClient(http) { BaseAddress = new Uri("https://notes.test/api/") };
        return (new NotesContext(new RestDataContextOptions<NotesContext>(model, client)), http);
    }

    public sealed class NotesContext(RestDataContextOptions<NotesContext> options) : RestDataContext(options)
    {
        public RestSet<Note> Notes => Set<Note>();
    }
}

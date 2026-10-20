using static LinqToRest.Client.UnitTests.ClientModelTests;

namespace LinqToRest.Client.UnitTests;

public sealed class WritePermissionTests
{
    [Fact]
    public void Add_to_a_set_without_inserts_throws()
    {
        var (context, _) = Create("\"update\"");

        var exception = Assert.Throws<InvalidOperationException>(() => context.Notes.Add(new Note()));

        Assert.Equal("The set 'notes' doesn't allow inserts.", exception.Message);
    }

    [Fact]
    public void Remove_from_a_set_without_deletes_throws_and_leaves_the_entity_untracked()
    {
        var (context, _) = Create("\"insert\", \"update\"");
        var note = new Note { Id = 1 };

        var exception = Assert.Throws<InvalidOperationException>(() => context.Notes.Remove(note));

        Assert.Equal("The set 'notes' doesn't allow deletes.", exception.Message);
        Assert.Equal(EntityState.Detached, context.Entry(note).State);
    }

    [Fact]
    public void Removing_an_added_entity_only_forgets_it()
    {
        var (context, _) = Create("\"insert\"");
        var note = new Note();
        context.Notes.Add(note);

        context.Notes.Remove(note);

        Assert.Equal(EntityState.Detached, context.Entry(note).State);
    }

    [Fact]
    public void Update_on_a_set_without_updates_throws()
    {
        var (context, _) = Create("\"insert\"");

        var exception = Assert.Throws<InvalidOperationException>(() => context.Notes.Update(new Note { Id = 1 }));

        Assert.Equal("The set 'notes' doesn't allow updates.", exception.Message);
    }

    [Fact]
    public async Task Saving_a_modified_entity_of_a_set_without_updates_throws_before_sending()
    {
        var (context, http) = Create("\"insert\"");
        var note = new Note { Id = 1, Text = "old" };
        context.Notes.Attach(note);
        note.Text = "new";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("The set 'notes' doesn't allow updates.", exception.Message);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Read_only_set_never_reports_changes()
    {
        var (context, http) = Create("");
        var note = new Note { Id = 1, Text = "old" };
        context.Notes.Attach(note);
        note.Text = "new";

        Assert.Equal(0, await context.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(EntityState.Unchanged, context.Entry(note).State);
        Assert.Empty(http.Requests);
    }

    private static (NotesContext Context, StubHandler Http) Create(string writes)
    {
        var model = ClientModel.Bind(Document(writes: writes), new Dictionary<string, Type> { ["notes"] = typeof(Note), ["authors"] = typeof(Author) });
        var http = new StubHandler();
        var client = new HttpClient(http) { BaseAddress = new Uri("https://notes.test/api/") };
        return (new NotesContext(new RestDataContextOptions<NotesContext>(model, client)), http);
    }

    public sealed class NotesContext(RestDataContextOptions<NotesContext> options) : RestDataContext(options)
    {
        public RestSet<Note> Notes => Set<Note>();
    }
}

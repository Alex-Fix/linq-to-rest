using static LinqToRest.Client.UnitTests.ClientModelTests;

namespace LinqToRest.Client.UnitTests;

/// <summary>The reflection-free binding generated contexts use: same validation as the Type-based one.</summary>
public sealed class SetBindingTests
{
    [Fact]
    public void Binds_through_delegates()
    {
        var model = ClientModel.Bind(Document(), Notes(), Authors());

        var notes = model.FindSet(typeof(Note))!;
        var note = (Note)notes.Create();
        notes.FindProperty("text")!.Set(note, "hello");
        notes.FindProperty("labels")!.Set(note, new List<string> { "a" });
        Assert.Equal("hello", notes.FindProperty("text")!.Get(note));
        Assert.Equal(["Id", "Text", "Kind", "Score", "Labels", "AuthorId"], notes.Properties.Select(property => property.Name));
        Assert.True(notes.Properties[0].IsKey);
        Assert.Same(model.FindSet(typeof(Author)), notes.Navigations.Single().Target);
        Assert.Same(notes.Properties[^1], notes.Navigations.Single().ForeignKey);
        Assert.Equal(["a"], note.Labels);
    }

    [Fact]
    public void Rejects_a_missing_member_binding()
    {
        var notes = new SetBinding<Note>("notes", static () => new Note())
            .Property("id", nameof(Note.Id), static n => n.Id, static (n, v) => n.Id = v);

        var error = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), notes, Authors()));

        Assert.Contains("Note, the class of the set 'notes', has no binding for 'text'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_binding_the_model_does_not_have()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(
            Document(),
            Notes().Property("local", nameof(Note.Local), static n => n.Local, static (n, v) => n.Local = v),
            Authors()));

        Assert.Contains("binds 'local', which the model doesn't have", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_missing_unknown_and_duplicate_sets()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), Notes()));
        var unknown = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), Notes(), Authors(), new SetBinding<Writer>("writers", static () => new Writer())));
        var twice = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), Notes(), Authors(), Authors()));

        Assert.Contains("No class is given for the set 'authors'", missing.Message, StringComparison.Ordinal);
        Assert.Contains("The model has no set 'writers'", unknown.Message, StringComparison.Ordinal);
        Assert.Contains("The set 'authors' is bound twice", twice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_member_of_another_type()
    {
        var notes = new SetBinding<Note>("notes", static () => new Note())
            .Property("id", nameof(Note.Id), static n => n.Id, static (n, v) => n.Id = v)
            .Property("text", nameof(Note.Text), static n => n.Text.Length, static (_, _) => { })
            .Property("kind", nameof(Note.Kind), static n => n.Kind, static (n, v) => n.Kind = v)
            .Property("score", nameof(Note.Score), static n => n.Score, static (n, v) => n.Score = v)
            .Values("labels", nameof(Note.Labels), static n => n.Labels, static (n, v) => n.Labels = v)
            .Property("authorId", nameof(Note.AuthorId), static n => n.AuthorId, static (n, v) => n.AuthorId = v)
            .Reference("author", nameof(Note.Author), static n => n.Author, static (n, v) => n.Author = v);

        var error = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), notes, Authors()));

        Assert.Contains("has 'Text' of type Int32, but the model says string", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_navigation_to_another_class()
    {
        var notes = new SetBinding<Note>("notes", static () => new Note())
            .Property("id", nameof(Note.Id), static n => n.Id, static (n, v) => n.Id = v)
            .Property("text", nameof(Note.Text), static n => n.Text, static (n, v) => n.Text = v)
            .Property("kind", nameof(Note.Kind), static n => n.Kind, static (n, v) => n.Kind = v)
            .Property("score", nameof(Note.Score), static n => n.Score, static (n, v) => n.Score = v)
            .Values("labels", nameof(Note.Labels), static n => n.Labels, static (n, v) => n.Labels = v)
            .Property("authorId", nameof(Note.AuthorId), static n => n.AuthorId, static (n, v) => n.AuthorId = v)
            .Reference<Writer>("author", nameof(Note.Author), static _ => null, static (_, _) => { });

        var error = Assert.Throws<InvalidOperationException>(() => ClientModel.Bind(Document(), notes, Authors()));

        Assert.Contains("has the navigation 'Author' of type Writer, but the model says Author", error.Message, StringComparison.Ordinal);
    }

    private static SetBinding<Note> Notes() => new SetBinding<Note>("notes", static () => new Note())
        .Property("id", nameof(Note.Id), static n => n.Id, static (n, v) => n.Id = v)
        .Property("text", nameof(Note.Text), static n => n.Text, static (n, v) => n.Text = v)
        .Property("kind", nameof(Note.Kind), static n => n.Kind, static (n, v) => n.Kind = v)
        .Property("score", nameof(Note.Score), static n => n.Score, static (n, v) => n.Score = v)
        .Values("labels", nameof(Note.Labels), static n => n.Labels, static (n, v) => n.Labels = v)
        .Property("authorId", nameof(Note.AuthorId), static n => n.AuthorId, static (n, v) => n.AuthorId = v)
        .Reference("author", nameof(Note.Author), static n => n.Author, static (n, v) => n.Author = v);

    private static SetBinding<Author> Authors() => new SetBinding<Author>("authors", static () => new Author())
        .Property("id", nameof(Author.Id), static a => a.Id, static (a, v) => a.Id = v);
}

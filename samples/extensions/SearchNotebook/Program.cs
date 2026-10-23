using System.Text.Json;
using System.Text.Json.Serialization;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions;
using CommandDeck.Extensions.Views;

await ExtensionApp.RunAsync( args, new Notebook( ) );

sealed class Notebook : IExtensionCommand, IExtensionSearchProvider
{
    private const string NoteId = "note";
    private const string NoteViewId = "note";
    private const string NotesProviderId = "notes";
    private const string SearchNoteId = "search-note";
    private const string EditActionId = "edit";
    private const string SaveActionId = "save";
    private const string TitleFieldId = "title";
    private const string BodyFieldId = "body";

    public string Id => "note";
    public string SearchId => NotesProviderId;
    private long _revision;
    private Note _note = new( "My searchable note", "" );
    public async ValueTask OpenAsync( CommandContext context, CancellationToken token )
    {
        var data = await context.Data.ReadAsync( token );
        _revision = data.Revision;

        if ( data.Values.TryGetValue( SearchNoteId, out var value ) )
        {
            _note = value.Deserialize( NotebookJson.Default.Note )!;
        }

        if ( context.Inputs.ContainsKey( "searchItemId" ) )
        {
            await ShowNoteAsync( context, token );
        }
        else
        {
            await ShowEditorAsync( context, token );
        }
    }
    public async ValueTask HandleAsync( CommandContext context, CommandEvent commandEvent, CancellationToken token )
    {
        if ( commandEvent is ActionInvoked { ActionId: EditActionId } )
        {
            await ShowEditorAsync( context, token );

            return;
        }

        if ( commandEvent is not FormSubmitted { ActionId: SaveActionId } form )
        {
            return;
        }

        var note = new Note( form.Values[TitleFieldId].GetString( )!, form.Values[BodyFieldId].GetString( )! );

        var result = await context.Data.WriteAsync( SearchNoteId, JsonSerializer.SerializeToElement( note, NotebookJson.Default.Note ), _revision, token );

        _revision = result.Revision;
        _note = note;
        await ShowNoteAsync( context, token );
    }

    public ValueTask<ExtensionSearchResponse> SearchAsync( SearchContext context, CancellationToken token )
    {
        token.ThrowIfCancellationRequested( );
        var note = context.Cache?.Deserialize( NotebookJson.Default.Note );
        var match = MatchesNoteQuery( note, context );

        return ValueTask.FromResult( new ExtensionSearchResponse( match ? [new( NoteId, note!.Title, "Saved notebook entry" )] : [] ) );
    }

    private ValueTask ShowEditorAsync( CommandContext context, CancellationToken token )
    {
        return context.Views.ReplaceAsync( new FormView
        {
            Id = NoteViewId,
            Title = "Edit searchable note",
            Submit = ViewAction.Callback( SaveActionId, "Save note" ),
            Fields = [FormField.Text( TitleFieldId, "Title", true, 120 ) with { DefaultValue = JsonSerializer.SerializeToElement( _note.Title, NotebookJson.Default.String ) },
            FormField.Multiline( BodyFieldId, "Note", 2048 ) with { DefaultValue = JsonSerializer.SerializeToElement( _note.Body, NotebookJson.Default.String ) }]
        }, token );
    }

    private ValueTask ShowNoteAsync( CommandContext context, CancellationToken token )
    {
        return context.Views.ReplaceAsync( new DetailView
        {
            Id = NoteViewId,
            Title = _note.Title,
            Body = _note.Body,
            Actions = [ViewAction.Callback( EditActionId, "Edit note" )]
        }, token );
    }
    private static bool MatchesNoteQuery( Note? note, SearchContext context )
    {
        return note is not null && context.Query
            .Split( ' ', StringSplitOptions.RemoveEmptyEntries )
            .All( term => (note.Title + " " + note.Body).Contains( term, StringComparison.OrdinalIgnoreCase ) );
    }
}

sealed record Note( string Title, string Body );

[JsonSerializable( typeof( Note ) )]
[JsonSerializable( typeof( string ) )]
sealed partial class NotebookJson : JsonSerializerContext;

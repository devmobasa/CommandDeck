using System.Text;
using System.Collections.Immutable;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions;
using CommandDeck.Extensions.Views;

await ExtensionApp.RunAsync( args, new NotesCommand( ), new TasksCommand( ) );

sealed class NotesCommand : IExtensionCommand
{
    private const string NotesConnection = "notes";
    private const string NotesViewId = "notes";
    private const string ReadActionId = "read";
    private const string RefreshActionId = "refresh";
    private const string NoteViewId = "note";
    private const string NoteErrorViewId = "note-error";

    public string Id => "notes";
    private ImmutableArray<FileEntry> _entries = [];
    public ValueTask OpenAsync( CommandContext context, CancellationToken token ) => ListAsync( context, 0, token );
    private async ValueTask ListAsync( CommandContext context, int offset, CancellationToken token )
    {
        try
        {
            var result = await context.Brokers.ListAsync( NotesConnection, offset: offset, cancellationToken: token );

            _entries = result.Entries;
            await context.Views.ReplaceAsync( CreateNotesView( result ), token );
        }
        catch ( Exception exception ) when ( exception is ExtensionRpcException or BrokerOutcomeException )
        {
            await context.Views.ReplaceAsync( new ListView { Id = NotesViewId, Title = "Select a notes root in Connections, then refresh", Items = [], Actions = [ViewAction.Callback( RefreshActionId, "Refresh" )] }, token );
        }
    }
    public async ValueTask HandleAsync( CommandContext context, CommandEvent value, CancellationToken token )
    {
        if ( value is PageRequested page )
        {
            await ListAsync( context, int.Parse( page.Token ), token );

            return;
        }

        if ( value is ActionInvoked { ActionId: RefreshActionId } )
        {
            await ListAsync( context, 0, token );

            return;
        }

        if ( value is not ActionInvoked { ActionId: ReadActionId, ItemId: { } id } )
        {
            return;
        }

        try
        {
            var entry = _entries.Single( entry => entry.Reference == id );
            var result = await context.Brokers.ReadAsync( NotesConnection, entry.Reference, token );
            await context.Views.PushAsync( CreateNoteView( entry, result ), token );
        }
        catch ( Exception exception ) when ( exception is ExtensionRpcException or BrokerOutcomeException or DecoderFallbackException or InvalidDataException )
        {
            await context.Views.PushAsync( new DetailView { Id = NoteErrorViewId, Title = "Note unavailable", Body = "The file changed, was revoked, or exceeds the UTF-8 text view's 64 KiB limit. Go Back and refresh." }, token );
        }
    }

    private ListView CreateNotesView( FileListing result )
    {
        return new ListView
        {
            Id = NotesViewId,
            Title = "Selected notes",
            Items = _entries
                .Where( entry => !entry.IsDirectory )
                .Select( entry => new ListItem
                {
                    Id = entry.Reference,
                    Title = entry.Name,
                    Actions = [ViewAction.Callback( ReadActionId, "Read note" )]
                } )
                .ToImmutableArray( ),
            NextPage = result.NextOffset?.ToString( ),
            Actions = [ViewAction.Callback( RefreshActionId, "Refresh" )]
        };
    }

    private static DetailView CreateNoteView( FileEntry entry, FileContent result )
    {
        return new DetailView
        {
            Id = NoteViewId,
            Title = entry.Name,
            Body = new UTF8Encoding( false, true )
                .GetString( result.Bytes.Span )
                .Replace( "\r\n", "\n", StringComparison.Ordinal )
        };
    }
}

sealed class TasksCommand : IExtensionCommand
{
    private const string TasksConnection = "tasks";
    private const string TasksViewId = "tasks";
    private const string TaskTitleProperty = "title";
    private const string PendingTaskKey = "pending";
    private const string TitleFieldId = "title";
    private const string TaskItemIdPrefix = "task";
    private const string RefreshActionId = "refresh";
    private const string NewTaskActionId = "new";
    private const string CreateTaskActionId = "create";

    public string Id => "tasks";
    private long _revision;
    private string _key = Guid
        .NewGuid( )
        .ToString( "N" );
    private FormView? _form;
    public async ValueTask OpenAsync( CommandContext context, CancellationToken token )
    {
        var data = await context.Data.ReadAsync( token );
        _revision = data.Revision;

        if ( data.Values.TryGetValue( PendingTaskKey, out var pending ) )
        {
            _key = pending
                .GetProperty( "key" )
                .GetString( )!;

            await FormAsync( context, pending
                .GetProperty( TaskTitleProperty )
                .GetString( )!, "A prior request may have completed. Check the task list before starting another request.", token );
        }
        else
        {
            await ListAsync( context, token );
        }
    }
    private static async ValueTask ListAsync( CommandContext context, CancellationToken token )
    {
        ImmutableArray<ListItem> items = [];
        var title = "Tasks";

        try
        {
            using var client = context.Brokers.CreateHttpClient( TasksConnection );
            using var response = await client.GetAsync( "tasks", token );
            response.EnsureSuccessStatusCode( );
            using var json = JsonDocument.Parse( await response.Content.ReadAsStringAsync( token ) );
            items = CreateTaskItems( json );
        }
        catch ( Exception exception ) when ( exception is ExtensionRpcException or HttpRequestException or JsonException )
        {
            title = "Tasks unavailable · check Connections, then refresh";
        }

        await context.Views.ReplaceAsync( CreateTasksView( items, title ), token );
    }
    private async ValueTask FormAsync( CommandContext context, string title, string? error, CancellationToken token )
    {
        _form = CreateTaskForm( title, error );
        await context.Views.ReplaceAsync( _form, token );
    }
    public async ValueTask HandleAsync( CommandContext context, CommandEvent value, CancellationToken token )
    {
        if ( value is ActionInvoked { ActionId: RefreshActionId } )
        {
            await ListAsync( context, token );

            return;
        }

        if ( value is ActionInvoked { ActionId: NewTaskActionId } )
        {
            _key = Guid
                .NewGuid( )
                .ToString( "N" );

            await FormAsync( context, "", null, token );

            return;
        }

        if ( value is not FormSubmitted submit ||
             _form is null )
        {
            return;
        }

        await SubmitTaskAsync( context, submit, _form, token );
    }

    private async ValueTask SubmitTaskAsync( CommandContext context, FormSubmitted submit, FormView form, CancellationToken token )
    {
        var title = submit.Values[TitleFieldId].GetString( )!;
        var pending = JsonElement.Parse( "{\"key\":" + JsonSerializer.Serialize( _key, SampleJson.Default.String ) + ",\"title\":" + JsonSerializer.Serialize( title, SampleJson.Default.String ) + "}" );

        try
        {
            var saved = await context.Data.WriteAsync( PendingTaskKey, pending, _revision, token );
            _revision = saved.Revision;
            var body = "{\"title\":" + JsonSerializer.Serialize( title, SampleJson.Default.String ) + "}";

            var result = await context.Brokers.SendHttpAsync( TasksConnection, new( "POST", "/tasks", Convert.ToBase64String( Encoding.UTF8.GetBytes( body ) ), System.Net.Mime.MediaTypeNames.Application.Json, _key ), token );
            if ( result.StatusCode is >= 200 and < 300 )
            {
                var cleared = await context.Data.DeleteAsync( PendingTaskKey, _revision, token );
                _revision = cleared.Revision;

                form = form with
                {
                    Title = "Task created",
                    Error = null,
                    ValidatedFormRevision = submit.FormRevision
                };
            }
            else
            {
                form = form with
                {
                    Error = "Request was rejected. Check the task list before creating another request. This request will not be retried automatically.",
                    ValidatedFormRevision = submit.FormRevision
                };
            }
        }
        catch ( BrokerOutcomeException exception )
        {
            form = form with
            {
                Error = "Outcome: " + exception.Outcome + ". Check the task list before creating another request. This request will not be retried automatically.",
                ValidatedFormRevision = submit.FormRevision
            };
        }
        catch ( ExtensionRpcException )
        {
            form = form with
            {
                Error = "Request unavailable. Check Connections and the task list before retrying.",
                ValidatedFormRevision = submit.FormRevision
            };
        }

        _form = form;
        await context.Views.ReplaceAsync( form, token );
    }

    private static ImmutableArray<ListItem> CreateTaskItems( JsonDocument json )
    {
        return json.RootElement
            .EnumerateArray( )
            .Take( 64 )
            .Select( ( value, index ) => new ListItem
            {
                Id = TaskItemIdPrefix + index,
                Title = value
                    .GetProperty( TaskTitleProperty )
                    .GetString( )!
            } )
            .ToImmutableArray( );
    }

    private static ListView CreateTasksView( ImmutableArray<ListItem> items, string title )
    {
        return new ListView
        {
            Id = TasksViewId,
            Title = title,
            Items = items,
            Actions = [ViewAction.Callback( RefreshActionId, "Refresh" ), ViewAction.Callback( NewTaskActionId, "New task" )]
        };
    }

    private static FormView CreateTaskForm( string title, string? error )
    {
        return new FormView
        {
            Id = TasksViewId,
            Title = "Create task",
            Fields = [FormField.Text( TitleFieldId, "Title", true, 120 ) with { DefaultValue = string.IsNullOrEmpty( title ) ? null : JsonElement.Parse( JsonSerializer.Serialize( title, SampleJson.Default.String ) ) }],
            Submit = ViewAction.Callback( CreateTaskActionId, "Create task" ),
            Actions = [ViewAction.Callback( RefreshActionId, "View tasks" )],
            Error = error
        };
    }
}

[System.Text.Json.Serialization.JsonSerializable( typeof( string ) )]
sealed partial class SampleJson : System.Text.Json.Serialization.JsonSerializerContext;

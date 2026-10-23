namespace CommandDeck;

// Clipboard writes: Copy, snippets (which may read the clipboard once to fill
// {clipboard}, or retained history for {clipboard offset=N}), and clipboard
// history's own row actions.
internal sealed class ClipboardActionHandler(
    ClipboardHistoryProvider? clipboardHistory,
    BoundedProcessRunner? snippetClipboardRunner,
    TimeProvider? timeProvider = null ) : ILinuxActionHandler
{
    private const string CopyActionId = "copy";
    private const string CopyActionTitle = "Copy";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private const int MaximumSnippetClipboardBytes = 64 * ByteUnits.BytesPerKibibyte;
    private const int MaximumSnippetClipboardErrorBytes = 4 * ByteUnits.BytesPerKibibyte;
    private const long MaximumSnippetClipboardProcessBytes = 128 * ByteUnits.BytesPerMebibyte;

    private static readonly string[] OwnedKinds =
    [
        ActionKinds.CopyText,
        ActionKinds.SnippetCopy,
        ActionKinds.SnippetPreview,
        ActionKinds.ClipboardHistory
    ];

    public IReadOnlyList<string> Kinds => OwnedKinds;

    public async ValueTask<ActionExecutionResult> ExecuteAsync(
        SearchResult result, CommandAction action, string query, CancellationToken cancellationToken ) => action.Kind switch
        {
            ActionKinds.CopyText => await CopyTextAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.SnippetCopy or ActionKinds.SnippetPreview => await ExecuteSnippetAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.ClipboardHistory => await ExecuteClipboardHistoryAsync( action, cancellationToken )
                .ConfigureAwait( false ),
            _ => LinuxActionArguments.UnknownKind( action )
        };

    private async ValueTask<ActionExecutionResult> ExecuteSnippetAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var payload = SnippetActionPayload.Read( action.Arguments );
        var template = SnippetTemplate.Compile( payload.Template );
        if ( RequiresUnapprovedClipboardHistory( payload, template ) )
        {
            return ActionExecutionResult.Failure( "Search for this snippet again before it reads clipboard history." );
        }

        Dictionary<int, string> clipboard = [];

        foreach ( var offset in template.ClipboardOffsets )
        {
            var (text, failure) = await ReadSnippetClipboardAsync( offset, cancellationToken )
                .ConfigureAwait( false );

            if ( failure is not null )
            {
                return failure;
            }

            clipboard[offset] = text!;
        }

        var expanded = template.Expand( action.Arguments, _time, clipboard );

        if ( action.Kind == ActionKinds.SnippetPreview )
        {
            return ActionExecutionResult.Success( "Snippet preview", expanded );
        }

        return await CopyTextAsync( CreateSnippetCopyAction( expanded ), cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask<(string? Text, ActionExecutionResult? Failure)> ReadCurrentClipboardAsync( CancellationToken cancellationToken )
    {
        ProcessExecutionLimits limits = CreateClipboardReadLimits( );
        ProcessResult read;

        if ( snippetClipboardRunner is null )
        {
            read = await ProcessRunner
                .RunBoundedAsync( "wl-paste", ["--no-newline", "--type", "text"], null, TimeSpan.FromSeconds( 2 ), limits, cancellationToken )
                .ConfigureAwait( false );
        }
        else
        {
            read = await snippetClipboardRunner( "wl-paste", ["--no-newline", "--type", "text"], TimeSpan.FromSeconds( 2 ), limits, cancellationToken )
                .ConfigureAwait( false );
        }

        if ( ClipboardReadFailed( read ) )
        {
            return (null, ActionExecutionResult.Failure( GetClipboardReadFailure( read ) ));
        }

        return (read.StandardOutput, null);
    }

    private (string? Text, ActionExecutionResult? Failure) ReadClipboardHistory( int offset )
    {
        if ( clipboardHistory is null )
        {
            return (null, ActionExecutionResult.Failure( "Clipboard history is unavailable." ));
        }

        var text = clipboardHistory.ReadRecentText( offset );
        if ( text is null )
        {
            return (null, ActionExecutionResult.Failure( $"Clipboard history has no text entry {offset} places back." ));
        }

        return (text, null);
    }

    private static Dictionary<string, string> SnippetClipboardEnvironment( )
    {
        Dictionary<string, string> environment = CreateClipboardEnvironment( );

        foreach ( var name in new[] { XdgEnvironmentNames.RuntimeDirectory, EnvironmentVariableNames.WaylandDisplay } )
        {
            var value = Environment.GetEnvironmentVariable( name );
            if ( !string.IsNullOrEmpty( value ) )
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private static async ValueTask<ActionExecutionResult> CopyTextAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var text = TextActionPayload.Read( action.Arguments ).Text;

        if ( !ProcessRunner.ExistsOnPath( ToolExecutables.ClipboardCopy ) )
        {
            return ActionExecutionResult.Failure( "wl-copy is not installed. Install the Arch package 'wl-clipboard'." );
        }

        return await ClipboardCopyProcess
            .CopyAsync( text, cancellationToken )
            .ConfigureAwait( false );
    }

    private static bool RequiresUnapprovedClipboardHistory( SnippetActionPayload payload, CompiledSnippet template )
    {
        return template.ReadsClipboardHistory && !payload.ReadsClipboardHistory;
    }

    private static bool ClipboardReadFailed( ProcessResult read )
    {
        return read is not { ExitCode: 0, TimedOut: false, AnyOutputLimitExceeded: false, MemoryLimitExceeded: false };
    }

    private static ProcessExecutionLimits CreateClipboardReadLimits( )
    {
        return new( )
        {
            MaximumStandardOutputBytes = MaximumSnippetClipboardBytes,
            MaximumStandardErrorBytes = MaximumSnippetClipboardErrorBytes,
            MaximumMemoryBytes = MaximumSnippetClipboardProcessBytes,
            ClearEnvironment = true,
            EnvironmentVariables = SnippetClipboardEnvironment( )
        };
    }

    private static Dictionary<string, string> CreateClipboardEnvironment( )
    {
        return ProcessEnvironmentDefaults.CreateEnvironment( Environment.GetEnvironmentVariable( EnvironmentVariableNames.ExecutablePath ),
            includeLocaleAll: false );
    }

    private async ValueTask<ActionExecutionResult> ExecuteClipboardHistoryAsync( CommandAction action, CancellationToken cancellationToken )
    {
        if ( clipboardHistory is null )
        {
            return ActionExecutionResult.Failure( "Clipboard history is unavailable." );
        }

        return await clipboardHistory
            .ExecuteAsync( action, cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask<(string? Text, ActionExecutionResult? Failure)> ReadSnippetClipboardAsync( int offset, CancellationToken cancellationToken )
    {
        if ( offset == 0 )
        {
            return await ReadCurrentClipboardAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        return ReadClipboardHistory( offset );
    }

    private static string GetClipboardReadFailure( ProcessResult read )
    {
        if ( read.AnyOutputLimitExceeded )
        {
            return "Clipboard text exceeds 64 KiB.";
        }

        return "Could not read clipboard text.";
    }

    private static CommandAction CreateSnippetCopyAction( string expanded )
    {
        return new CommandAction
        {
            Id = CopyActionId,
            Title = CopyActionTitle,
            Kind = ActionKinds.CopyText,
            Arguments = new TextActionPayload( expanded ).ToArguments( )
        };
    }
}

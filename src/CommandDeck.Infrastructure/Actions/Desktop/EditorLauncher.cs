namespace CommandDeck;

// Opens a file at a line in the editor Settings selects, or in $VISUAL/$EDITOR
// when no preference store is available. Edit at Line and a systemd unit's
// Edit both open files this way.
internal sealed class EditorLauncher( ILauncherSearchPreferences? preferences )
{
    public async ValueTask<ActionExecutionResult> EditAtLineAsync( CommandAction action, CancellationToken cancellationToken )
    {
        var location = EditorActionPayload.Read( action.Arguments );
        var path = location.Path;
        var line = location.Line.ToString( System.Globalization.CultureInfo.InvariantCulture );
        var column = location.Column.ToString( System.Globalization.CultureInfo.InvariantCulture );
        var key = location.Key;

        if ( preferences is not null )
        {
            var launcher = await preferences
                .GetPreferencesAsync( cancellationToken )
                .ConfigureAwait( false );

            var selected = SelectEditor( launcher );
            selected = EditorAdapterValidator.Validate( selected, selected.Terminal );
            var expanded = EditorAdapterValidator.Expand( selected, path, line, column, key );

            if ( selected.Terminal )
            {
                if ( !ProcessRunner.ExistsOnPath( ToolExecutables.TerminalLauncher ) )
                {
                    return ActionExecutionResult.Failure( "The terminal editor requires xdg-terminal-exec, which is unavailable." );
                }

                return await ManagedLaunch
                    .RunAndReportAsync( ToolExecutables.TerminalLauncher, ["--", selected.Executable, .. expanded], null, cancellationToken )
                    .ConfigureAwait( false );
            }

            return await ManagedLaunch
                .RunAndReportAsync( selected.Executable, expanded, null, cancellationToken )
                .ConfigureAwait( false );
        }

        var editorValue = ResolveEnvironmentEditor( );

        var editor = editorValue.Split( ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
        if ( editor.Length == 0 )
        {
            return ActionExecutionResult.Failure( "No editor is configured." );
        }

        var arguments = editor
            .Skip( 1 )
            .ToList( );

        var adapter = CreateEnvironmentAdapter( editor[0] );
        arguments.AddRange( EditorAdapterValidator.Expand( adapter, path, line, column, key ) );

        return await ManagedLaunch
            .RunAndReportAsync( editor[0], arguments, null, cancellationToken )
            .ConfigureAwait( false );
    }

    private static EditorAdapter CreateEnvironmentAdapter( string executable )
    {
        var executableName = Path.GetFileName( executable );
        if ( executableName is "code" or "codium" )
        {
            return EditorAdapter.CreateGraphicalDefault( );
        }

        if ( executableName is "nvim" or "vim" or "vi" )
        {
            return EditorAdapter.CreateTerminalDefault( );
        }

        return new EditorAdapter { Executable = executable, Arguments = [EditorAdapter.GenericLineTemplate, EditorArgumentTokens.Path] };
    }

    private static EditorAdapter SelectEditor( LauncherPreferences launcher )
    {
        if ( launcher.PreferredEditor == EditorKinds.Terminal )
        {
            return launcher.TerminalEditor;
        }

        return launcher.GraphicalEditor;
    }

    private static string ResolveEnvironmentEditor( )
    {
        return Environment.GetEnvironmentVariable( "VISUAL" ) ?? Environment.GetEnvironmentVariable( "EDITOR" ) ?? EditorAdapter.TerminalExecutable;
    }
}

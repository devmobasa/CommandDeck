using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace CommandDeck;

public sealed partial class HerdrClient
{
    private const int MaximumProjectPanes = 1024;

    public async ValueTask<HerdrFocusOutcome> OpenProjectAsync( string path, CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<ActionExecutionResult>>? activateWindow = null )
    {
        cancellationToken.ThrowIfCancellationRequested( );
        var directory = NormalizeProjectDirectory( path );
        if ( IsProjectDirectoryMissing( directory ) )
        {
            return ProjectFailure( "The selected project folder is unavailable." );
        }

        if ( !_available( ) )
        {
            return ProjectFailure( "Herdr is not on the daemon PATH." );
        }

        // Reject overlapping opens so two invocations cannot both create from
        // the same empty inventory, or queue an unexpected later focus change.
        if ( !TryBeginFocus( ) )
        {
            return FocusBusy( );
        }

        try
        {
            var workspaceResult = StartWorkspaceListing( cancellationToken );
            var paneResult = StartPaneList( cancellationToken );

            await Task
                .WhenAll( workspaceResult, paneResult )
                .ConfigureAwait( false );

            cancellationToken.ThrowIfCancellationRequested( );
            var matches = FindProjectSpaces( workspaceResult.Result, paneResult.Result, directory );
            if ( matches is null )
            {
                return ProjectFailure( "Herdr's project inventory is unavailable or incomplete. No space was created." );
            }

            var targetId = matches.FirstOrDefault( );
            if ( targetId is null )
            {
                targetId = await CreateProjectSpaceAsync( directory, cancellationToken )
                    .ConfigureAwait( false );

                if ( targetId is null )
                {
                    return ProjectFailure( "Herdr did not confirm project creation; it may already have happened. Check Herdr before trying again." );
                }
            }

            cancellationToken.ThrowIfCancellationRequested( );

            return await FocusAndActivateAsync( HerdrTargetKinds.Workspace, targetId, activateWindow, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return ProjectFailure( "Herdr could not complete the project action. Check Herdr before trying again." );
        }
        finally
        {
            EndFocus( );
        }
    }

    private static List<string>? FindProjectSpaces( ProcessResult workspaceResult, ProcessResult paneResult, string directory )
    {
        var workspaces = ReadResultArray( workspaceResult, "workspace_list", "workspaces" );
        using var workspaceDocument = workspaces?.Document;
        var panes = ReadResultArray( paneResult, "pane_list", "panes" );
        using var paneDocument = panes?.Document;
        if ( workspaces is null ||
             panes is null ||
             HasInvalidProjectInventories( workspaces.Value.Items, panes.Value.Items, workspaceDocument!, paneDocument! ) )
        {
            return null;
        }

        Dictionary<string, (bool Focused, long Number, bool Match, long ExpectedPanes, int SeenPanes)> spaces = new( StringComparer.Ordinal );

        foreach ( var space in workspaces.Value.Items.EnumerateArray( ) )
        {
            var id = ReadString( space, WorkspaceIdField );
            var paneCount = ReadInteger( space, "pane_count" );
            if ( paneCount is null || IsInvalidProjectWorkspace( spaces, id, paneCount.Value ) )
            {
                return null;
            }

            var checkout = ReadCheckoutPath( space );

            spaces.Add( id!,
                (ReadBoolean( space, "focused" ) == true, ReadCount( space, "number" ), NormalizeProjectDirectory( checkout ) == directory, paneCount.Value,
                    0) );
        }

        HashSet<string> paneIds = new( StringComparer.Ordinal );

        foreach ( var pane in panes.Value.Items.EnumerateArray( ) )
        {
            var workspaceId = ReadString( pane, WorkspaceIdField );
            var paneId = ReadString( pane, "pane_id" );
            if ( !TryRegisterPane( spaces, paneIds, workspaceId, paneId, out var space ) )
            {
                return null;
            }

            // cwd is Herdr's reported shell directory; foreground_cwd can be
            // a transient child process directory and is not project identity.
            space.Match |= NormalizeProjectDirectory( ReadString( pane, "cwd" ) ) == directory;
            space.SeenPanes++;
            spaces[workspaceId] = space;
        }

        if ( spaces.Values.Any( static space => space.SeenPanes != space.ExpectedPanes ) )
        {
            return null;
        }

        return OrderMatchingWorkspaces( spaces );
    }

    private async ValueTask<string?> CreateProjectSpaceAsync( string directory, CancellationToken cancellationToken )
    {
        var name = Path.GetFileName( directory );
        if ( name.Length == 0 )
        {
            name = "Root";
        }

        var result = await RunAsync( [WorkspaceCommand, CreateCommand, CwdOption, directory, LabelOption, name, NoFocusOption], FocusTimeout,
                FocusStandardOutputBytes, FocusStandardErrorBytes, cancellationToken )
            .ConfigureAwait( false );

        if ( HasProjectRequestFailed( result ) )
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse( result.StandardOutput );
            var envelope = document.RootElement;
            if ( CannotReadCreatedWorkspace( envelope, out var workspace, out var pane ) )
            {
                return null;
            }

            var id = ReadString( workspace, WorkspaceIdField );
            if ( IsInvalidCreatedWorkspace( directory, name, workspace, pane, id ) )
            {
                return null;
            }

            return id;
        }
        catch ( JsonException )
        {
            return null;
        }
    }

    private static string? NormalizeProjectDirectory( string? path )
    {
        if ( IsInvalidProjectPath( path ) )
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator( Path.GetFullPath( path ) );
    }

    private static HerdrFocusOutcome ProjectFailure( string message ) => new( ) { Succeeded = false, Message = message };

    private Task<ProcessResult> StartPaneList( CancellationToken cancellationToken )
    {
        return RunAsync( [PaneCommand, ListCommand], ListTimeout, ProjectListOutputBytes, TabListStandardErrorBytes, cancellationToken )
            .AsTask( );
    }

    private static bool IsInvalidProjectWorkspace( Dictionary<string, (bool Focused, long Number, bool Match, long ExpectedPanes, int SeenPanes)> spaces,
        string? id, long paneCount )
    {
        return !IsValidIdentifier( id ) || spaces.ContainsKey( id! ) || paneCount is < 0 or > MaximumProjectPanes;
    }

    private static string? ReadCheckoutPath( JsonElement space )
    {
        if ( space.TryGetProperty( "worktree", out var worktree ) )
        {
            return ReadString( worktree, "checkout_path" );
        }

        return null;
    }

    private static List<string> OrderMatchingWorkspaces( Dictionary<string, (bool Focused, long Number, bool Match, long ExpectedPanes, int SeenPanes)> spaces )
    {
        return spaces
            .Where( static pair => pair.Value.Match )
            .OrderByDescending( static pair => pair.Value.Focused )
            .ThenBy( static pair => pair.Value.Number )
            .ThenBy( static pair => pair.Key, StringComparer.Ordinal )
            .Select( static pair => pair.Key )
            .ToList( );
    }

    private static bool HasProjectRequestFailed( ProcessResult result )
    {
        return result.ExitCode != 0 || result.TimedOut || result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static bool HasInvalidProjectInventories( JsonElement workspaces, JsonElement panes, JsonDocument workspaceDocument, JsonDocument paneDocument )
    {
        return workspaces.GetArrayLength( ) > MaximumWorkspaces ||
               panes.GetArrayLength( ) > MaximumProjectPanes ||
               workspaceDocument.RootElement.TryGetProperty( "error", out _ ) ||
               paneDocument.RootElement.TryGetProperty( "error", out _ );
    }

    private static bool IsInvalidCreatedWorkspace( string directory, string name, JsonElement workspace, JsonElement pane, string? id )
    {
        return !IsValidIdentifier( id ) ||
               ReadString( workspace, "label" ) != name ||
               ReadString( pane, WorkspaceIdField ) != id ||
               !IsValidIdentifier( ReadString( pane, "pane_id" ) ) ||
               NormalizeProjectDirectory( ReadString( pane, "cwd" ) ) != directory;
    }

    private static bool IsInvalidProjectPath( [NotNullWhen( false )] string? path )
    {
        return string.IsNullOrWhiteSpace( path ) || path.Length > MaximumPathCharacters || path.Any( char.IsControl ) || !Path.IsPathFullyQualified( path );
    }

    private static bool IsProjectDirectoryMissing( [NotNullWhen( false )] string? directory )
    {
        return directory is null || !Directory.Exists( directory );
    }

    private static bool TryRegisterPane( Dictionary<string, (bool Focused, long Number, bool Match, long ExpectedPanes, int SeenPanes)> spaces,
        HashSet<string> paneIds, [NotNullWhen( true )] string? workspaceId, [NotNullWhen( true )] string? paneId,
        out (bool Focused, long Number, bool Match, long ExpectedPanes, int SeenPanes) space )
    {
        space = default;

        return IsValidIdentifier( paneId ) && paneIds.Add( paneId! ) && workspaceId is not null && spaces.TryGetValue( workspaceId, out space );
    }

    private static bool CannotReadCreatedWorkspace( JsonElement envelope, out JsonElement workspace, out JsonElement pane )
    {
        workspace = default;
        pane = default;

        return envelope.ValueKind != JsonValueKind.Object ||
               envelope.TryGetProperty( "error", out _ ) ||
               !envelope.TryGetProperty( "result", out var payload ) ||
               ReadString( payload, "type" ) != "workspace_created" ||
               !payload.TryGetProperty( "workspace", out workspace ) ||
               !payload.TryGetProperty( "root_pane", out pane );
    }
}

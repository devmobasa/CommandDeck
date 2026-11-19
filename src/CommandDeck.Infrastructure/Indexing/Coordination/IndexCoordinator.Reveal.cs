namespace CommandDeck;

public sealed partial class IndexCoordinator
{
    private const int RevealInspectionItems = 256;
    private const int MaximumRevealPathCharacters = 512;

    // Opens the folder that holds an index issue. The daemon resolves the issue
    // from its own inspection and refuses a redacted or clipped path, so a
    // shortened prefix can never name another folder. The opened folder is the
    // issue's parent, or its nearest existing ancestor, never above the root.
    public async ValueTask RevealIndexIssueAsync( string workItemId, CancellationToken cancellationToken )
    {
        var inspection = await InspectAsync( 0, RevealInspectionItems, cancellationToken )
            .ConfigureAwait( false );

        var item = inspection.WorkItems.FirstOrDefault( work => string.Equals( work.Id, workItemId, StringComparison.Ordinal ) );
        if ( item is null )
        {
            throw new InvalidOperationException( "That issue is no longer listed. Refresh the inspector." );
        }

        if ( !IsRevealablePath( item ) )
        {
            throw new InvalidOperationException( "That path is private or too long to open." );
        }

        var folder = FindRevealFolder( item );
        if ( folder is null || !_files.Exclusions.TryAuthorizeReveal( folder, out var canonical ) )
        {
            throw new InvalidOperationException( "That folder cannot be opened." );
        }

        var result = await ManagedLaunch
            .LaunchApplicationAsync( new DesktopEntryCommand { Executable = ToolExecutables.DesktopOpener, Arguments = [canonical] }, terminal: false,
                cancellationToken )
            .ConfigureAwait( false );

        if ( !result.Succeeded )
        {
            throw new InvalidOperationException( result.Message ?? "The file manager could not be opened." );
        }
    }

    private static bool IsRevealablePath( IndexWorkItem item )
    {
        return item.Root != IndexingConstants.RedactedDiagnosticPath && item.RelativePath != IndexingConstants.RedactedDiagnosticPath
            && item.Root.Length < MaximumRevealPathCharacters && item.RelativePath.Length < MaximumRevealPathCharacters
            && Path.IsPathFullyQualified( item.Root );
    }

    private static string? FindRevealFolder( IndexWorkItem item )
    {
        var root = Path.TrimEndingDirectorySeparator( Path.GetFullPath( item.Root ) );
        var target = Path.GetFullPath( Path.Join( root, item.RelativePath ) );
        if ( !ExcludedFolderPaths.IsSameOrDescendant( root, target ) )
        {
            return null;
        }

        var folder = Path.GetDirectoryName( target ) ?? root;

        while ( !Directory.Exists( folder ) && folder.Length > root.Length )
        {
            folder = Path.GetDirectoryName( folder ) ?? root;
        }

        return ExcludedFolderPaths.IsSameOrDescendant( root, folder ) ? folder : null;
    }
}

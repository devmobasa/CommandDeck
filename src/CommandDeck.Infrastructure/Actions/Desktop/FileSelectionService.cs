using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

// Selection admits only results from the selecting client's own search
// snapshots; resolve receives that client and the requested result ID.
public sealed class FileSelectionService( IActionExecutor actions, Func<string, string, SearchResult?> resolve )
{
    private const int MaximumSelectionCount = 32;

    private const string CopyActionId = "copy";
    private const string SelectionTargetId = "file-selection";

    private readonly object _gate = new( );
    private readonly Dictionary<string, List<FileSelectionItem>> _clients = new( StringComparer.Ordinal );

    public List<FileSelectionItem> Select( string client, string resultId, bool enabled )
    {
        lock ( _gate )
        {
            if ( !_clients.TryGetValue( client, out var selection ) )
            {
                _clients[client] = selection = [];
            }

            var result = resolve( client, resultId );

            if ( !enabled )
            {
                selection.RemoveAll( item => item.Id == resultId || item.Path == result?.Metadata.GetValueOrDefault( ResultMetadataKeys.Path ) );
            }
            else
            {
                var path = result?.Metadata.GetValueOrDefault( ResultMetadataKeys.Path );
                if ( IsInvalidSelection( result, path ) )
                {
                    throw new InvalidOperationException( "Select a current file result first." );
                }

                if ( !selection.Any( item => item.Path == path ) )
                {
                    if ( selection.Count >= MaximumSelectionCount )
                    {
                        throw new InvalidOperationException( "Select at most 32 files at once." );
                    }

                    selection.Add( new( result.Id, path, result.Title ) );
                }
            }

            return [.. selection];
        }
    }

    public void Clear( string client )
    {
        lock ( _gate )
        {
            _clients.Remove( client );
        }
    }

    public async ValueTask<ActionExecutionResult> ExecuteAsync( string client, string actionId, CancellationToken cancellationToken )
    {
        List<FileSelectionItem> selection;

        lock ( _gate )
        {
            selection = _clients.TryGetValue( client, out var current ) ? [.. current] : [];
        }

        if ( selection.Count == 0 )
        {
            return ActionExecutionResult.Failure( "Select some files first." );
        }

        if ( HasMissingFiles( selection ) )
        {
            return ActionExecutionResult.Failure( "A selected path no longer exists. Remove it from the selection and try again." );
        }

        SearchResult target = CreateSelectionTarget( );

        if ( actionId == FileSelectionActionIds.CopyPaths )
        {
            return await actions
                .ExecuteAsync( target, CreateCopyPathsAction( selection ), "", cancellationToken )
                .ConfigureAwait( false );
        }

        if ( actionId != FileSelectionActionIds.Open )
        {
            return ActionExecutionResult.Failure( "Unknown file selection action." );
        }

        var opened = 0;

        foreach ( var item in selection )
        {
            var outcome = await actions
                .ExecuteAsync( target, CreateOpenFileAction( item ), "", cancellationToken )
                .ConfigureAwait( false );

            if ( !outcome.Succeeded )
            {
                return ActionExecutionResult.Failure( $"Opened {opened} of {selection.Count} paths. {outcome.Message}" );
            }

            opened++;
        }

        return ActionExecutionResult.Success( $"Opened {opened} paths." );
    }

    private static bool IsInvalidSelection( [NotNullWhen( false )] SearchResult? result, [NotNullWhen( false )] string? path )
    {
        return result is null ||
               !result.Actions.Any( static action => action.Kind == ActionKinds.CollectFile ) ||
               string.IsNullOrEmpty( path ) ||
               !Path.IsPathFullyQualified( path );
    }

    private static bool HasMissingFiles( List<FileSelectionItem> selection )
    {
        return selection.Any( static item => !File.Exists( item.Path ) && !Directory.Exists( item.Path ) );
    }

    private static SearchResult CreateSelectionTarget( )
    {
        return new( )
        {
            Id = SelectionTargetId,
            Title = "Selected files",
            Kind = ResultKinds.File,
            ProviderId = ProviderIds.Folders,
            SourceId = SourceIds.Folders
        };
    }

    private static CommandAction CreateCopyPathsAction( List<FileSelectionItem> selection )
    {
        return new CommandAction
        {
            Id = CopyActionId,
            Title = "Copy paths",
            Kind = ActionKinds.CopyText,
            Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Text] = string.Join( '\n', selection.Select( static item => item.Path ) ) }
        };
    }

    private static CommandAction CreateOpenFileAction( FileSelectionItem item )
    {
        return new CommandAction
        {
            Id = FileSelectionActionIds.Open,
            Title = "Open file",
            Kind = ActionKinds.OpenPath,
            Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Path] = item.Path }
        };
    }
}

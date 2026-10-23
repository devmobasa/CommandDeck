using System.Text.Json;

namespace CommandDeck;

public sealed class HyprlandProvider : ICommandProvider
{
    private const string WindowPrefix = "window:";
    private const string AddressPrefix = "address:";
    private const string WorkspacePrefix = "workspace:";

    private const string HaystackMetadata = "haystack";
    private const string SwitchActionId = "switch";
    private const string FocusActionId = "focus";
    private const string CloseActionId = "close";
    private const double ActiveWindowBrowseScore = 0.35;
    private const double ActiveWorkspaceBoost = 0.03;
    private const double ActiveWorkspaceBrowseScore = 0.36;
    private const double OccupiedWorkspaceScore = 0.19;
    private const double EmptyWorkspaceScore = 0.18;
    private const int MaximumResults = 100;
    private const double WindowBrowseScore = 0.2;

    // Hyprland reports an empty title for windows that never set one.
    private const string UntitledWindowTitle = "(untitled window)";

    private readonly IApplicationIconResolver? _iconResolver;

    public HyprlandProvider( IApplicationIconResolver? iconResolver = null )
    {
        _iconResolver = iconResolver;
    }

    public string Id => ProviderIds.Hyprland;

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        if ( !ProcessRunner.ExistsOnPath( ToolExecutables.Hyprctl ) )
        {
            return [];
        }

        var clientsTask = StartClientsQuery( cancellationToken );
        var workspacesTask = StartWorkspacesQuery( cancellationToken );

        await Task
            .WhenAll( clientsTask, workspacesTask )
            .ConfigureAwait( false );

        List<SearchResult> results = [];

        await ParseClientsAsync( clientsTask.Result.StandardOutput, query.Text, request.ActiveWorkspace, results, cancellationToken )
            .ConfigureAwait( false );

        ParseWorkspaces( workspacesTask.Result.StandardOutput, query.Text, request.ActiveWorkspace, results );

        return OrderResults( request, results );
    }

    private async ValueTask ParseClientsAsync( string json, string query, string? activeWorkspace, List<SearchResult> results,
        CancellationToken cancellationToken )
    {
        if ( string.IsNullOrWhiteSpace( json ) )
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse( json );
            var windowClasses = ReadApplicationClasses( document );

            var icons = await ResolveWindowIconsAsync( windowClasses, cancellationToken )
                .ConfigureAwait( false );

            foreach ( var client in document.RootElement.EnumerateArray( ) )
            {
                var address = JsonElementReader.ReadString( client, "address" ) ?? string.Empty;
                var title = JsonElementReader.ReadString( client, "title" );
                if ( string.IsNullOrWhiteSpace( title ) )
                {
                    title = UntitledWindowTitle;
                }

                var appClass = GetApplicationClass( client );
                var workspace = GetWorkspaceLabel( client );
                var isActiveWorkspace = IsActiveWorkspace( workspace, activeWorkspace );
                var score = Math.Max( FuzzyMatcher.Score( query, title ), FuzzyMatcher.Score( query, appClass ) );
                if ( IsUnmatchedQuery( query, score ) )
                {
                    continue;
                }

                results.Add( CreateWindowResult( query, address, title, appClass, workspace, icons,
                    isActiveWorkspace, score ) );
            }
        }
        catch ( JsonException )
        {
        }
    }

    private async ValueTask<IReadOnlyDictionary<string, string>> ResolveWindowIconsAsync( IReadOnlyList<string> windowClasses,
        CancellationToken cancellationToken )
    {
        if ( _iconResolver is null )
        {
            return windowClasses.ToDictionary( static windowClass => windowClass, static windowClass => windowClass.ToLowerInvariant( ),
                StringComparer.OrdinalIgnoreCase );
        }

        return await _iconResolver
            .ResolveApplicationIconsAsync( windowClasses, cancellationToken )
            .ConfigureAwait( false );
    }

    private static string GetApplicationClass( JsonElement client ) =>
        JsonElementReader.ReadString( client, "class" ) ?? JsonElementReader.ReadString( client, "initialClass" ) ?? "window";

    private static void ParseWorkspaces( string json, string query, string? activeWorkspace, List<SearchResult> results )
    {
        if ( string.IsNullOrWhiteSpace( json ) )
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse( json );
            foreach ( var workspace in document.RootElement.EnumerateArray( ) )
            {
                var id = GetNumberOrString( workspace, "id" ) ?? "?";
                var name = JsonElementReader.ReadString( workspace, "name" ) ?? id;
                var windows = GetNumberOrString( workspace, "windows" ) ?? "0";
                var monitor = JsonElementReader.ReadString( workspace, "monitor" ) ?? "unknown";
                var title = $"Workspace {name}";
                var occupied = int.TryParse( windows, out var windowCount ) && windowCount > 0;
                var isActiveWorkspace = MatchesActiveWorkspace( activeWorkspace, id, name );
                var score = Math.Max( FuzzyMatcher.Score( query, title ), FuzzyMatcher.Score( query, monitor ) );
                if ( IsUnmatchedQuery( query, score ) )
                {
                    continue;
                }

                results.Add( CreateWorkspaceResult( query, workspace, id, name, windows, monitor,
                    title, occupied, isActiveWorkspace, score ) );
            }
        }
        catch ( JsonException )
        {
        }
    }

    private static string? GetNumberOrString( JsonElement element, string property )
    {
        if ( !element.TryGetProperty( property, out var value ) )
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString( ),
            JsonValueKind.Number => value.GetRawText( ),
            _ => null
        };
    }

    private static string? GetNestedStringOrNumber( JsonElement element, string parent, string property )
    {
        if ( element.TryGetProperty( parent, out var nested ) )
        {
            return GetNumberOrString( nested, property );
        }
        else
        {
            return null;
        }
    }

    private static bool IsActiveWorkspace( string candidate, string? activeWorkspace ) =>
        !string.IsNullOrWhiteSpace( activeWorkspace ) && string.Equals( candidate, activeWorkspace, StringComparison.OrdinalIgnoreCase );

    private static int WorkspaceNumber( SearchResult result )
    {
        if ( TryReadWorkspaceNumber( result, out var number ) )
        {
            return number;
        }
        else
        {
            return int.MaxValue;
        }
    }

    private static Task<ProcessResult> StartClientsQuery( CancellationToken cancellationToken )
    {
        return ProcessRunner
            .RunAsync( ToolExecutables.Hyprctl, ["-j", "clients"], standardInput: null, workingDirectory: null, TimeSpan.FromMilliseconds( 250 ),
                cancellationToken )
            .AsTask( );
    }

    private static Task<ProcessResult> StartWorkspacesQuery( CancellationToken cancellationToken )
    {
        return ProcessRunner
            .RunAsync( ToolExecutables.Hyprctl, ["-j", "workspaces"], standardInput: null, workingDirectory: null, TimeSpan.FromMilliseconds( 250 ),
                cancellationToken )
            .AsTask( );
    }

    private static SearchResult[] OrderResults( SearchRequest request, List<SearchResult> results )
    {
        return results
            .OrderByDescending( static result => result.Score )
            .ThenBy( static result => WorkspaceNumber( result ) )
            .ThenBy( static result => result.Title, StringComparer.OrdinalIgnoreCase )
            .Take( Math.Clamp( request.Limit, 1, MaximumResults ) )
            .ToArray( );
    }

    private static string[] ReadApplicationClasses( JsonDocument document )
    {
        return document
            .RootElement.EnumerateArray( )
            .Select( GetApplicationClass )
            .Distinct( StringComparer.OrdinalIgnoreCase )
            .ToArray( );
    }

    private static string GetWorkspaceLabel( JsonElement client )
    {
        return GetNestedStringOrNumber( client, "workspace", "name" ) ?? GetNestedStringOrNumber( client, "workspace", "id" ) ?? "?";
    }

    private static bool IsUnmatchedQuery( string query, double score )
    {
        return !string.IsNullOrWhiteSpace( query ) && score <= 0;
    }

    private static bool MatchesActiveWorkspace( string? activeWorkspace, string id, string name )
    {
        return IsActiveWorkspace( id, activeWorkspace ) || IsActiveWorkspace( name, activeWorkspace );
    }

    private static SearchResult CreateWindowResult( string query, string address, string title, string appClass, string workspace,
        IReadOnlyDictionary<string, string> icons, bool isActiveWorkspace, double score )
    {
        return new SearchResult
        {
            Id = $"{WindowPrefix}{address}",
            Kind = ResultKinds.Window,
            Title = title,
            Subtitle = $"{appClass} · workspace {workspace}",
            Icon = icons.GetValueOrDefault( appClass, appClass.ToLowerInvariant( ) ),
            ProviderId = ProviderIds.Hyprland,
            SourceId = SourceIds.WindowsWorkspaces,
            Score = GetWindowScore( query, isActiveWorkspace, score ),
            Details =
            [
                new DetailField( "Class", appClass, Monospace: true ),
                new DetailField( "Workspace", workspace ),
                new DetailField( "Address", address, Monospace: true )
            ],
            Actions =
            [
                new CommandAction
                {
                    Id = FocusActionId,
                    Title = "Focus Window",
                    Kind = ActionKinds.HyprlandDispatch,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                    {
                        [HyprlandActionOperations.OperationKey] = HyprlandActionOperations.FocusWindow,
                        [HyprlandActionOperations.ArgumentKey] = $"{AddressPrefix}{address}"
                    }
                },
                new CommandAction
                {
                    Id = CloseActionId,
                    Title = "Close Window",
                    Kind = ActionKinds.HyprlandDispatch,
                    IsDestructive = true,
                    RequiresConfirmation = true,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                    {
                        [HyprlandActionOperations.OperationKey] = HyprlandActionOperations.CloseWindow,
                        [HyprlandActionOperations.ArgumentKey] = $"{AddressPrefix}{address}"
                    }
                }
            ],
            Metadata = new Dictionary<string, string>( StringComparer.Ordinal )
            {
                [ResultMetadataKeys.Address] = address,
                ["class"] = appClass,
                [ResultMetadataKeys.Workspace] = workspace,
                [HaystackMetadata] = $"{title} {appClass} {workspace}"
            }
        };
    }

    private static SearchResult CreateWorkspaceResult( string query, JsonElement workspace, string id, string name, string windows, string monitor,
        string title, bool occupied, bool isActiveWorkspace, double score )
    {
        return new SearchResult
        {
            Id = $"{WorkspacePrefix}{id}",
            Kind = ResultKinds.Workspace,
            Title = title,
            Subtitle = $"{windows} windows · {monitor}",
            Icon = "view-grid",
            ProviderId = ProviderIds.Hyprland,
            SourceId = SourceIds.WindowsWorkspaces,
            Score = GetWorkspaceScore( query, occupied, isActiveWorkspace, score ),
            Details =
            [
                new DetailField( "Monitor", monitor, Monospace: true ),
                new DetailField( "Windows", windows )
            ],
            Actions =
            [
                new CommandAction
                {
                    Id = SwitchActionId,
                    Title = "Switch Workspace",
                    Kind = ActionKinds.HyprlandDispatch,
                    Arguments = new Dictionary<string, string>( StringComparer.Ordinal )
                    {
                        [HyprlandActionOperations.OperationKey] = HyprlandActionOperations.SwitchWorkspace, [HyprlandActionOperations.ArgumentKey] = name
                    }
                }
            ],
            Metadata = WorkspacePreviewIdentity.CreateMetadata( workspace )
        };
    }

    private static double GetWindowScore( string query, bool isActiveWorkspace, double score )
    {
        if ( string.IsNullOrWhiteSpace( query ) )
        {
            if ( isActiveWorkspace )
            {
                return ActiveWindowBrowseScore;
            }

            return WindowBrowseScore;
        }

        return score + (isActiveWorkspace ? ActiveWorkspaceBoost : 0);
    }

    private static double GetWorkspaceScore( string query, bool occupied, bool isActiveWorkspace, double score )
    {
        if ( string.IsNullOrWhiteSpace( query ) )
        {
            if ( isActiveWorkspace )
            {
                return ActiveWorkspaceBrowseScore;
            }

            if ( occupied )
            {
                return OccupiedWorkspaceScore;
            }

            return EmptyWorkspaceScore;
        }

        return score + (isActiveWorkspace ? ActiveWorkspaceBoost : 0);
    }

    private static bool TryReadWorkspaceNumber( SearchResult result, out int number )
    {
        number = default;

        return result.Kind == ResultKinds.Workspace &&
               result.Metadata.TryGetValue( ResultMetadataKeys.WorkspaceName, out var name ) &&
               int.TryParse( name, out number ) &&
               number > 0;
    }
}

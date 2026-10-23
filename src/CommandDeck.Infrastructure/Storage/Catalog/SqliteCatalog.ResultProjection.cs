using System.Text.Json;

namespace CommandDeck;

public sealed partial class SqliteCatalog
{
    private const string LaunchActionId = "launch";
    private const string OpenActionId = "open";
    private const string CollectActionId = "collect";
    private const string BrowseParentActionId = "browse-parent";
    private const string RevealActionId = "reveal";
    private const string CopyPathActionId = "copy-path";
    private const string CopyFileUrlActionId = "copy-file-url";
    private const string EditActionId = "edit";
    private const string CopyValueActionId = "copy-value";
    private const string CopyKeyActionId = "copy-key";
    private const string CopyAssignmentActionId = "copy-assignment";
    private const string OpenSubmenuActionId = "open-submenu";
    private const string OpenNativeMenuActionId = "open-native-menu";
    private const string RunActionId = "run";

    // The row a catalog search would return for this indexed item, for
    // providers that list catalog items from their own source.
    internal static SearchResult ProjectIndexedItem( IndexedItem item, double score, CatalogItemState? state ) => CreateCatalogSearchResult( item.StableId,
        item.Kind, item.Title, item.Subtitle, item.Path, item.Icon, item.PayloadJson,
        item.ProviderId, item.SourceId, score, state?.IsFavorite ?? false, state is null ? [] : [.. state.Aliases] );

    private static SearchResult CreateCatalogSearchResult( string stableId, string kind, string title, string? subtitle, string? path, string? icon,
        string? payload, string providerId, string sourceId, double score, bool isFavorite, List<string> aliases, string? exactAlias = null,
        RankingExplanation? ranking = null )
    {
        var metadata = BuildMetadata( path, payload );

        if ( exactAlias is not null )
        {
            metadata[ResultMetadataKeys.ExactAlias] = exactAlias;
        }

        return new SearchResult
        {
            Id = stableId,
            Kind = kind,
            Title = title,
            Subtitle = subtitle,
            Icon = icon,
            ProviderId = providerId,
            SourceId = sourceId,
            IsFavorite = isFavorite,
            Aliases = aliases,
            CanCustomize = CanCustomizeResult( kind, sourceId, stableId ),
            Score = score,
            MatchedRanges = TitleHighlightRanges( ranking ),
            Ranking = ranking ?? new RankingExplanation { FinalScore = score },
            Details = BuildDetails( kind, path, metadata ),
            Actions = BuildActions( stableId, kind, title, path, payload ),
            Metadata = metadata
        };
    }

    private static bool CanCustomizeResult( string kind, string sourceId, string stableId )
    {
        return CanCustomize( kind, sourceId ) && !ClipboardSequenceCommands.IsCommand( stableId );
    }

    private static List<MatchedRange> TitleHighlightRanges( RankingExplanation? ranking )
    {
        return ranking?.Matches.FirstOrDefault( static match => match.Field == RankingFieldNames.Title )
                   ?.Ranges ??
               [];
    }

    private static List<DetailField> BuildDetails( string kind, string? path, IReadOnlyDictionary<string, string> metadata )
    {
        List<DetailField> details = [];

        switch ( kind )
        {
            case ResultKinds.Application:
                AddDetail( details, "Desktop file", GetValueOrFallback( metadata, ResultMetadataKeys.Application.DesktopFile, path ), monospace: true );
                AddDetail( details, "Desktop ID", metadata.GetValueOrDefault( ResultMetadataKeys.Application.DesktopId ), true );
                AddDetail( details, "Command", metadata.GetValueOrDefault( ResultMetadataKeys.Application.Exec ), true );
                AddDetail( details, "Terminal", FormatBoolean( metadata.GetValueOrDefault( ResultMetadataKeys.Application.Terminal ) ) );

                break;
            case ResultKinds.Configuration:
                AddDetail( details, "File", path, monospace: true );
                AddDetail( details, "Key location", FormatLocation( metadata, ResultMetadataKeys.KeyLine, ResultMetadataKeys.KeyColumn ), true );
                AddDetail( details, "Value location", FormatLocation( metadata, ResultMetadataKeys.ValueLine, ResultMetadataKeys.ValueColumn ), true );
                AddDetail( details, "Key", metadata.GetValueOrDefault( ResultMetadataKeys.Key ), true );
                AddDetail( details, "Section", metadata.GetValueOrDefault( ResultMetadataKeys.Section ), true );
                AddDetail( details, "Value", metadata.GetValueOrDefault( ResultMetadataKeys.Value ) );

                break;
            case ResultKinds.Omarchy:
                AddDetail( details, "Menu", metadata.GetValueOrDefault( ResultMetadataKeys.OmarchyMenuPath ) );
                AddDetail( details, "Entries", metadata.GetValueOrDefault( ResultMetadataKeys.OmarchyMenuItems ) );
                AddDetail( details, "Command", metadata.GetValueOrDefault( ActionArgumentNames.Command ), true );
                AddDetail( details, "Source", GetValueOrFallback( metadata, ResultMetadataKeys.Source, path ), true );

                break;
            case ResultKinds.Quicklink:
                AddDetail( details, "URL", metadata.GetValueOrDefault( ActionArgumentNames.UrlTemplate ), true );
                AddDetail( details, "Alias", metadata.GetValueOrDefault( ActionArgumentNames.Alias ), true );

                break;
            case ResultKinds.CustomCommand:
                var argv = CommandDetail( metadata );
                AddDetail( details, "Command", argv, true );
                AddDetail( details, "Working directory", metadata.GetValueOrDefault( ActionArgumentNames.WorkingDirectory ), true );
                AddDetail( details, "Confirmation", FormatBoolean( metadata.GetValueOrDefault( ActionArgumentNames.Confirm ) ) );

                break;
            default:
                AddDetail( details, "Path", path, monospace: true );

                break;
        }

        return details;
    }

    private static string? CommandDetail( IReadOnlyDictionary<string, string> metadata )
    {
        return metadata
            .GetValueOrDefault( ActionArgumentNames.Argv )
            ?.Replace( '\u001f', ' ' );
    }

    private static void AddDetail( List<DetailField> details, string label, string? value, bool monospace = false )
    {
        if ( !string.IsNullOrWhiteSpace( value ) )
        {
            details.Add( new DetailField( label, value, monospace ) );
        }
    }

    private static string? FormatBoolean( string? value )
    {
        if ( string.IsNullOrWhiteSpace( value ) )
        {
            return null;
        }

        if ( string.Equals( value, "true", StringComparison.OrdinalIgnoreCase ) )
        {
            return "Yes";
        }

        return "No";
    }

    private static string? GetValueOrFallback( IReadOnlyDictionary<string, string> values, string key, string? fallback ) =>
        values.TryGetValue( key, out var value ) ? value : fallback;

    private static List<CommandAction> BuildActions( string stableId, string kind, string title, string? path, string? payload )
    {
        List<CommandAction> actions = [];
        var values = ReadPayload( payload );

        if ( IsClipboardNavigationCommand( stableId, kind ) )
        {
            return [ClipboardHistoryNavigation.CreateAction( )];
        }

        if ( IsClipboardSequenceCommand( stableId, kind ) )
        {
            return [ClipboardSequenceCommands.CreateAction( stableId )];
        }

        switch ( kind )
        {
            case ResultKinds.Application:
                values[ResultMetadataKeys.Application.Name] = title;
                actions.Add( CreateResultAction( LaunchActionId, "Launch", ActionKinds.LaunchDesktop, values ) );

                break;
            case ResultKinds.File:
                if ( !string.IsNullOrWhiteSpace( path ) )
                {
                    actions.Add( CreateResultAction( OpenActionId, "Open", ActionKinds.OpenPath, new PathActionPayload( path ).ToArguments( ) ) );

                    actions.Add( CreateResultAction( CollectActionId, "Toggle file selection", ActionKinds.CollectFile,
                        new PathActionPayload( path ).ToArguments( ) ) );

                    actions.Add( CreateResultAction( BrowseParentActionId, "Browse containing folder", ActionKinds.BrowseFolder,
                        new PathActionPayload( Path.GetDirectoryName( path ) ?? path ).ToArguments( ) ) );

                    actions.Add( CreateResultAction( RevealActionId, "Reveal", ActionKinds.RevealPath, new PathActionPayload( path ).ToArguments( ) ) );
                    actions.Add( CreateResultAction( CopyPathActionId, "Copy Path", ActionKinds.CopyText, new TextActionPayload( path ).ToArguments( ) ) );

                    actions.Add( CreateResultAction( CopyFileUrlActionId, "Copy File URL", ActionKinds.CopyText,
                        new TextActionPayload( new Uri( Path.GetFullPath( path ) ).AbsoluteUri ).ToArguments( ) ) );
                }

                break;
            case ResultKinds.Configuration:
                if ( !string.IsNullOrWhiteSpace( path ) )
                {
                    Dictionary<string, string> editArguments = ConfigurationActionArguments( path, values, title );

                    actions.Add( CreateResultAction( EditActionId, "Edit at Key", ActionKinds.EditAtLine, editArguments ) );

                    actions.Add( CreateResultAction( CopyValueActionId, "Copy Value", ActionKinds.CopyText,
                        new TextActionPayload( values.GetValueOrDefault( ResultMetadataKeys.Value, title ) ).ToArguments( ) ) );

                    actions.Add( CreateResultAction( CopyKeyActionId, "Copy Key", ActionKinds.CopyText,
                        new TextActionPayload( values.GetValueOrDefault( ResultMetadataKeys.Key, title ) ).ToArguments( ) ) );

                    if ( values.TryGetValue( ResultMetadataKeys.Assignment, out var assignment ) && !string.IsNullOrWhiteSpace( assignment ) )
                    {
                        actions.Add( CreateResultAction( CopyAssignmentActionId, "Copy Assignment", ActionKinds.CopyText,
                            new TextActionPayload( assignment ).ToArguments( ) ) );
                    }

                    actions.Add( CreateResultAction( RevealActionId, "Reveal", ActionKinds.RevealPath, new PathActionPayload( path ).ToArguments( ) ) );
                }

                break;
            case ResultKinds.Omarchy:
                if ( !string.IsNullOrWhiteSpace( values.GetValueOrDefault( ResultMetadataKeys.OmarchyMenu ) ) )
                {
                    actions.Add( CreateResultAction( OpenSubmenuActionId, "Open Submenu", ActionKinds.BrowseOmarchyMenu,
                        new( StringComparer.Ordinal )
                        {
                            [ResultMetadataKeys.OmarchyMenu] = values[ResultMetadataKeys.OmarchyMenu],
                            [ResultMetadataKeys.OmarchyMenuPath] = values.GetValueOrDefault( ResultMetadataKeys.OmarchyMenuPath, title )
                        } ) );
                }
                else if ( values.GetValueOrDefault( ResultMetadataKeys.NativeRoute ) == "true" )
                {
                    actions.Add( CreateResultAction( OpenNativeMenuActionId, "Open Native Menu", ActionKinds.RunArgv, values ) );
                }
                else
                {
                    actions.Add( CreateResultAction( RunActionId, "Run", ActionKinds.RunTrustedShell, values ) );
                }

                break;
            case ResultKinds.Quicklink:
                actions.Add( CreateResultAction( OpenActionId, "Open", ActionKinds.OpenUrl, values ) );

                break;
            case ResultKinds.CustomCommand:
                actions.Add( new CommandAction
                {
                    Id = RunActionId,
                    Title = "Run",
                    Kind = ActionKinds.RunArgv,
                    Arguments = values,
                    RequiresConfirmation = values.GetValueOrDefault( ActionArgumentNames.Confirm ) == "true"
                } );

                break;
        }

        if ( NeedsPathFallbackAction( actions, path ) )
        {
            actions.Add( CreateResultAction( OpenActionId, "Open", ActionKinds.OpenPath, new PathActionPayload( path ).ToArguments( ) ) );
        }

        return actions;
    }

    private static bool IsClipboardNavigationCommand( string stableId, string kind )
    {
        return stableId == ClipboardHistoryNavigation.Id && kind == ResultKinds.CustomCommand;
    }

    private static bool IsClipboardSequenceCommand( string stableId, string kind )
    {
        return ClipboardSequenceCommands.IsCommand( stableId ) && kind == ResultKinds.CustomCommand;
    }

    private static Dictionary<string, string> ConfigurationActionArguments( string path, Dictionary<string, string> values, string title )
    {
        return new Dictionary<string, string>( StringComparer.Ordinal )
        {
            [ActionArgumentNames.Path] = path,
            [ActionArgumentNames.Line] = values.GetValueOrDefault( ResultMetadataKeys.KeyLine, values.GetValueOrDefault( ResultMetadataKeys.Line, "1" ) ),
            [ActionArgumentNames.Column] =
                values.GetValueOrDefault( ResultMetadataKeys.KeyColumn, values.GetValueOrDefault( ResultMetadataKeys.Column, "1" ) ),
            [ActionArgumentNames.Key] = values.GetValueOrDefault( ResultMetadataKeys.Key, title )
        };
    }

    private static bool NeedsPathFallbackAction( List<CommandAction> actions, [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] string? path )
    {
        return actions.Count == 0 && !string.IsNullOrWhiteSpace( path );
    }

    private static CommandAction CreateResultAction( string id, string actionTitle, string actionKind, Dictionary<string, string> arguments )
    {
        return new CommandAction( )
        {
            Id = id,
            Title = actionTitle,
            Kind = actionKind,
            Arguments = arguments
        };
    }

    private static string? FormatLocation( IReadOnlyDictionary<string, string> metadata, string lineKey, string columnKey )
    {
        var line = metadata.GetValueOrDefault( lineKey ) ?? metadata.GetValueOrDefault( ResultMetadataKeys.Line );
        if ( string.IsNullOrWhiteSpace( line ) )
        {
            return null;
        }

        var column = metadata.GetValueOrDefault( columnKey ) ?? metadata.GetValueOrDefault( ResultMetadataKeys.Column );

        return string.IsNullOrWhiteSpace( column ) ? line : $"{line}:{column}";
    }

    private static Dictionary<string, string> BuildMetadata( string? path, string? payload )
    {
        var values = ReadPayload( payload );

        if ( !string.IsNullOrWhiteSpace( path ) )
        {
            values[ResultMetadataKeys.Path] = path;
        }

        return values;
    }

    private static Dictionary<string, string> ReadPayload( string? payload )
    {
        Dictionary<string, string> result = new( StringComparer.Ordinal );

        if ( string.IsNullOrWhiteSpace( payload ) )
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse( payload );
            foreach ( var property in document.RootElement.EnumerateObject( ) )
            {
                result[property.Name] = ReadPayloadValue( property );
            }
        }
        catch ( JsonException )
        {
            // An old/corrupt payload must not break search.
        }

        return result;
    }

    private static string ReadPayloadValue( JsonProperty property )
    {
        return property.Value.ValueKind switch
        {
            JsonValueKind.String => property.Value.GetString( ) ?? string.Empty,
            JsonValueKind.Number => property.Value.GetRawText( ),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Array => string.Join( "\u001f", property
                .Value.EnumerateArray( )
                .Select( static value => value.GetString( ) ?? string.Empty ) ),
            _ => property.Value.GetRawText( )
        };
    }

    internal static List<string> SplitAliases( string value )
    {
        if ( string.IsNullOrEmpty( value ) )
        {
            return [];
        }

        return value
            .Split( '\u001f', StringSplitOptions.RemoveEmptyEntries )
            .ToList( );
    }

    internal static bool CanCustomize( string kind, string sourceId ) => sourceId != SourceIds.OmarchySystem &&
                                                                         kind is ResultKinds.Application
                                                                             or ResultKinds.File
                                                                             or ResultKinds.Configuration
                                                                             or ResultKinds.Omarchy
                                                                             or ResultKinds.Quicklink
                                                                             or ResultKinds.CustomCommand
                                                                             or ResultKinds.ExtensionCommand;
}

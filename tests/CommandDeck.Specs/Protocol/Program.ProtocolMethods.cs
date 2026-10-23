using System.Diagnostics;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async ValueTask<UnixProtocolClient> ConnectWithRetryAsync( string socket )
    {
        var startupDeadline = TimeSpan.FromSeconds( 5 );
        var started = Stopwatch.GetTimestamp( );
        while ( true )
        {
            try
            {
                return await UnixProtocolClient.ConnectAsync( socket, CancellationToken.None );
            }
            catch ( System.Net.Sockets.SocketException ) when ( Stopwatch.GetElapsedTime( started ) < startupDeadline )
            {
                await Task.Delay( 25 );
            }
        }
    }

    private static async Task VerifyPreviewProtocolAsync( UnixProtocolClient client, string socket, string editorCapture, CancellationToken cancellationToken )
    {
        await client.SendAsync( new ProtocolRequest { RequestId = 4, Operation = ProtocolOperations.Search, Query = "preview-note" }, CancellationToken.None );

        SearchResult? fileResult = null;
        await foreach ( var line in client.ReadRawMessagesAsync( cancellationToken ) )
        {
            var message = DeserializeMessage( line );
            if ( message is { Type: "result", RequestId: 4, Result.Kind: "file" } )
            {
                fileResult = message.Result;
            }

            if ( message is { Type: "done", RequestId: 4 } )
            {
                break;
            }
        }

        True( fileResult is not null );
        await client.SendAsync( CreatePreviewRequest( fileResult! ), CancellationToken.None );

        var previewMessage = await ReadMessageAsync( client, requestId: 5, "preview-result", cancellationToken );

        Equal( "# Preview\n\nProtocol content", previewMessage?.Preview?.Text );
        Equal( "markdown", previewMessage?.Preview?.Language );

        await VerifyPreviewEditorProtocolAsync( client, socket, fileResult!.Id, fileResult.InvocationReference!, editorCapture, cancellationToken );

        await VerifyImagePreviewProtocolAsync( client, cancellationToken );
        await client.SendAsync( CreateForgedPreviewRequest( fileResult ), CancellationToken.None );
        var forgedError = await ReadMessageAsync( client, requestId: 6, "error", cancellationToken );
        True( forgedError?.Error?.Contains( "expired", StringComparison.Ordinal ) == true );

        await client.SendRawAsync( """{"version":3,"requestId":601,"operation":"/forbidden/path/sentinel"}""", CancellationToken.None );

        var safeError = await ReadMessageAsync( client, 601, "error", cancellationToken );
        Equal( "The request could not be completed safely.", safeError?.Error );

        static ProtocolRequest CreatePreviewRequest( SearchResult fileResult )
        {
            return new ProtocolRequest
            {
                RequestId = 5,
                Operation = ProtocolOperations.Preview,
                ResultId = fileResult.Id,
                InvocationReference = fileResult.InvocationReference
            };
        }

        static ProtocolRequest CreateForgedPreviewRequest( SearchResult fileResult )
        {
            return new ProtocolRequest
            {
                RequestId = 6,
                Operation = ProtocolOperations.Preview,
                ResultId = "file:forged",
                InvocationReference = fileResult.InvocationReference
            };
        }
    }

    private static async Task<ProtocolMessage?> ReadMessageAsync( UnixProtocolClient client, long requestId, string expectedType,
        CancellationToken cancellationToken )
    {
        ProtocolMessage? expected = null;
        await foreach ( var line in client.ReadRawMessagesAsync( cancellationToken ) )
        {
            var message = DeserializeMessage( line );
            if ( message?.RequestId == requestId && message.Type == expectedType )
            {
                expected = message;
            }

            if ( message?.RequestId == requestId && message.Type == "error" && expectedType != "error" )
            {
                throw CreateException( requestId, expectedType, message );
            }

            if ( message?.RequestId == requestId && message.Type == "done" )
            {
                return expected;
            }
        }

        return expected;

        static InvalidOperationException CreateException( long requestId, string expectedType, ProtocolMessage message )
        {
            return new InvalidOperationException( $"Protocol request {requestId} returned an error while awaiting " +
                                                  $"{expectedType}: {message.Error ?? "No error detail."}" );
        }
    }

    private static async Task VerifyPersonalizationProtocolAsync( UnixProtocolClient client, ILauncherPreferenceStore preferencesStore, string database,
        CancellationToken cancellationToken )
    {
        await VerifyBootstrapSourcesAsync( client, cancellationToken );

        await client.SendAsync( new ProtocolRequest { RequestId = 8, Operation = ProtocolOperations.Search, Query = "preview-note" }, CancellationToken.None );

        var result = await ReadMessageAsync( client, 8, "result", cancellationToken );

        ArgumentNullException.ThrowIfNull( result );
        ArgumentNullException.ThrowIfNull( result.Result );

        True( result.Result.CanCustomize );
        await VerifyFavoriteAliasAndOrderingAsync( client, result.Result, cancellationToken );

        await VerifyOrphanAndMalformedFavoritesAsync( client, preferencesStore, result.Result, cancellationToken );

        await VerifyPreferencesExportAsync( client, cancellationToken );
        await VerifyRejectedCustomizationMutationsAsync( client, cancellationToken );
        await VerifyInvalidPreferencePayloadsAsync( client, cancellationToken );

        await VerifyCustomizationOverflowAndResetAsync( client, preferencesStore, database, cancellationToken );

        await VerifyFallbackPreferencesProtocolAsync( client, cancellationToken );
    }

    private static async Task VerifyBootstrapSourcesAsync( UnixProtocolClient client, CancellationToken cancellationToken )
    {
        await client.SendAsync( new ProtocolRequest { RequestId = 7, Operation = ProtocolOperations.Bootstrap }, CancellationToken.None );

        var bootstrap = await ReadMessageAsync( client, 7, "bootstrap-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( bootstrap );
        ArgumentNullException.ThrowIfNull( bootstrap.Bootstrap );

        Equal( LauncherSourceRegistry.Configurable.Count, bootstrap.Bootstrap.Preferences.Sources.Count );
        Equal( "System Menu", bootstrap.Bootstrap.Sources.Single( value => value.Id == "omarchy-system" ).Label );
        Equal( "Services", bootstrap.Bootstrap.Sources.Single( value => value.Id == "systemd-units" ).Label );
        Equal( "Timezone", bootstrap.Bootstrap.Sources.Single( value => value.Id == "timezone" ).Label );
        Equal( "Emoji", bootstrap.Bootstrap.Sources.Single( value => value.Id == "emoji" ).Label );
        True( bootstrap.Bootstrap.Filters.Any( value => value.Id == "system-menu" ) );
        True( bootstrap.Bootstrap.Filters.Any( value => value.Id == "services" ) );
        True( bootstrap.Bootstrap.Filters.Any( value => value.Id == "timezone" ) );
        True( bootstrap.Bootstrap.Filters.Any( value => value.Id == "emoji" ) );
    }

    private static async Task VerifyFavoriteAliasAndOrderingAsync( UnixProtocolClient client, SearchResult result, CancellationToken cancellationToken )
    {
        await client.SendAsync( CreateProtocolFavoriteSetRequest( result ), CancellationToken.None );
        var favorite = await ReadMessageAsync( client, 9, "customization-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( favorite );
        ArgumentNullException.ThrowIfNull( favorite.Customization );

        True( favorite.Customization.IsFavorite );
        await client.SendAsync( CreateProtocolAliasSetRequest( result ), CancellationToken.None );
        var alias = await ReadMessageAsync( client, 10, "customization-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( alias );
        ArgumentNullException.ThrowIfNull( alias.Customization );

        Equal( "note", alias.Customization.Aliases[0] );
        await VerifyHiddenItemsProtocolAsync( client, result.Id, cancellationToken );
        await client.SendAsync( CreateProtocolFavoriteReorderRequest( result ), CancellationToken.None );
        var reordered = await ReadMessageAsync( client, 108, "preferences-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( reordered?.Preferences );

        Equal( result.Id, reordered.Preferences.Customizations[0].StableId );
        await VerifyFavoriteMoveProtocolAsync( client, result.Id, cancellationToken );
    }

    private static async Task VerifyOrphanAndMalformedFavoritesAsync( UnixProtocolClient client, ILauncherPreferenceStore preferencesStore, SearchResult result,
        CancellationToken cancellationToken )
    {
        SearchResult unavailableFavorite = CreateProtocolUnavailableFavorite( );
        await preferencesStore.SetFavoriteAsync( unavailableFavorite, true, CancellationToken.None );
        await client.SendAsync( CreateProtocolFavoriteRemoveOrphanRequest( ), CancellationToken.None );

        var removedOrphan = await ReadMessageAsync( client, 109, "customization-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( removedOrphan?.Customization );

        Equal( "file:protocol-orphan", removedOrphan.Customization.StableId );
        await client.SendAsync( CreateProtocolFavoriteToggleRequest( result ), CancellationToken.None );
        var malformedFavorite = await ReadMessageAsync( client, 101, "error", cancellationToken );

        ArgumentNullException.ThrowIfNull( malformedFavorite?.Error );

        True( malformedFavorite.Error.Contains( "requires enabled", StringComparison.Ordinal ) );
    }

    private static async Task VerifyPreferencesExportAsync( UnixProtocolClient client, CancellationToken cancellationToken )
    {
        await client.SendAsync( new ProtocolRequest { RequestId = 11, Operation = ProtocolOperations.PreferencesGet }, CancellationToken.None );

        var preferences = await ReadMessageAsync( client, 11, "preferences-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( preferences );
        ArgumentNullException.ThrowIfNull( preferences.Preferences );

        Equal( 1, preferences.Preferences.Customizations.Count );
        True( preferences.Preferences.Customizations[0].IsFavorite );

        Equal( "note", preferences
            .Preferences.Customizations[0]
            .Aliases[0] );

        await client.SendAsync( new ProtocolRequest { RequestId = 12, Operation = ProtocolOperations.CustomizationsExport }, CancellationToken.None );

        var export = await ReadMessageAsync( client, 12, "export-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( export );
        ArgumentNullException.ThrowIfNull( export.Export );

        Equal( 3, export.Export.Version );

        Equal( "note", export
            .Export.Customizations[0]
            .Aliases[0] );
    }

    private static async Task VerifyRejectedCustomizationMutationsAsync( UnixProtocolClient client, CancellationToken cancellationToken )
    {
        await client.SendAsync( CreateProtocolForgedFavoriteRequest( ), CancellationToken.None );
        var forged = await ReadMessageAsync( client, 13, "error", cancellationToken );

        ArgumentNullException.ThrowIfNull( forged );
        ArgumentNullException.ThrowIfNull( forged.Error );

        True( forged.Error.Contains( "expired", StringComparison.Ordinal ) );

        await client.SendAsync( new ProtocolRequest { RequestId = 102, Operation = ProtocolOperations.CustomizationsReset }, CancellationToken.None );

        var rejectedReset = await ReadMessageAsync( client, 102, "error", cancellationToken );

        ArgumentNullException.ThrowIfNull( rejectedReset?.Error );

        True( rejectedReset.Error.Contains( "confirmed", StringComparison.Ordinal ) );

        await client.SendAsync( new ProtocolRequest { RequestId = 103, Operation = ProtocolOperations.PreferencesGet }, CancellationToken.None );

        var unchanged = await ReadMessageAsync( client, 103, "preferences-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( unchanged?.Preferences );

        Equal( 1, unchanged.Preferences.Customizations.Count );

        Equal( "note", unchanged
            .Preferences.Customizations[0]
            .Aliases[0] );
    }

    private static async Task VerifyInvalidPreferencePayloadsAsync( UnixProtocolClient client, CancellationToken cancellationToken )
    {
        await client.SendRawAsync( """{"version":3,"requestId":104,"operation":"favorite.reorder","stableIds":null}""", CancellationToken.None );

        var nullStableIds = await ReadMessageAsync( client, 104, "error", cancellationToken );

        ArgumentNullException.ThrowIfNull( nullStableIds?.Error );

        True( nullStableIds.Error.Contains( "stableIds", StringComparison.Ordinal ) );

        await client.SendRawAsync( """{"version":3,"requestId":105,"operation":"preferences.update","preferences":{"sources":null}}""",
            CancellationToken.None );

        var nullSources = await ReadMessageAsync( client, 105, "error", cancellationToken );

        ArgumentNullException.ThrowIfNull( nullSources?.Error );

        True( nullSources.Error.Contains( "preferences.sources", StringComparison.Ordinal ) );

        await client.SendRawAsync( """{"version":3,"requestId":106,"operation":"preferences.update","preferences":{"sources":[null]}}""",
            CancellationToken.None );

        var nullSource = await ReadMessageAsync( client, 106, "error", cancellationToken );

        ArgumentNullException.ThrowIfNull( nullSource?.Error );

        True( nullSource.Error.Contains( "preferences.sources", StringComparison.Ordinal ) );
    }

    private static async Task VerifyCustomizationOverflowAndResetAsync( UnixProtocolClient client, ILauncherPreferenceStore preferencesStore, string database,
        CancellationToken cancellationToken )
    {
        SearchResult oversizedFavorite = CreateProtocolOversizedFavorite( );
        await preferencesStore.SetFavoriteAsync( oversizedFavorite, true, CancellationToken.None );

        await client.SendAsync( new ProtocolRequest { RequestId = 107, Operation = ProtocolOperations.CustomizationsExport }, CancellationToken.None );

        var oversizedExport = await ReadMessageAsync( client, 107, "error", cancellationToken );

        ArgumentNullException.ThrowIfNull( oversizedExport?.Error );

        True( oversizedExport.Error.Contains( "256 KiB", StringComparison.Ordinal ) );
        await VerifyLegacyCustomizationOverflowProtocolAsync( client, database, cancellationToken );

        await client.SendAsync( new ProtocolRequest { RequestId = 14, Operation = ProtocolOperations.CustomizationsReset, Confirmed = true },
            CancellationToken.None );

        var reset = await ReadMessageAsync( client, 14, "preferences-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( reset );
        ArgumentNullException.ThrowIfNull( reset.Preferences );

        Equal( 0, reset.Preferences.Customizations.Count );
    }

    private static async Task VerifyFallbackPreferencesProtocolAsync( UnixProtocolClient client, CancellationToken cancellationToken )
    {
        var fallbackPreferences = LauncherPreferences.CreateDefault( );
        fallbackPreferences = new LauncherPreferences { Sources = fallbackPreferences.Sources, WebFallbackQuicklinkId = "web" };

        await client.SendAsync( new ProtocolRequest { RequestId = 15, Operation = ProtocolOperations.PreferencesUpdate, Preferences = fallbackPreferences },
            CancellationToken.None );

        _ = await ReadMessageAsync( client, 15, "preferences-result", cancellationToken );
        const string fallbackQuery = "definitely-no-commanddeck-match-82f44";

        await client.SendAsync( new ProtocolRequest { RequestId = 16, Operation = ProtocolOperations.Search, Query = fallbackQuery }, CancellationToken.None );

        var fallback = await ReadMessageAsync( client, 16, "result", cancellationToken );

        ArgumentNullException.ThrowIfNull( fallback?.Result );

        Equal( "fallback", fallback.Result.ProviderId );
        True( !fallback.Result.CanCustomize );

        Equal( $"https://example.test/search?q={Uri.EscapeDataString( fallbackQuery )}", fallback
            .Result.Actions[0]
            .Arguments["urlTemplate"] );

        await client.SendAsync( CreateFileOnlySearchRequest( fallbackQuery ), CancellationToken.None );
        var explicitFallback = false;
        await foreach ( var line in client.ReadRawMessagesAsync( cancellationToken ) )
        {
            var message = DeserializeMessage( line );
            if ( message is { RequestId: 17, Type: "result" } )
            {
                explicitFallback = true;
            }

            if ( message is { RequestId: 17, Type: "done" } )
            {
                break;
            }
        }

        True( !explicitFallback );

        static ProtocolRequest CreateFileOnlySearchRequest( string fallbackQuery )
        {
            return new ProtocolRequest
            {
                RequestId = 17,
                Operation = ProtocolOperations.Search,
                Query = fallbackQuery,
                Expression = new QueryExpression { IncludeFilterIds = [SearchFilterIds.Files] }
            };
        }
    }
}

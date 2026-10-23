using Microsoft.Data.Sqlite;

namespace CommandDeck;

internal sealed partial class SqliteLauncherPreferenceStore
{
    private const string HideAfterActionKey = "hide_after_action";
    private const string ClipboardHistoryShortcutKey = "clipboard_history_shortcut";
    private const string ReduceMotionKey = "reduce_motion";
    private const string WorkspacePreviewModeKey = "workspace_preview_mode";
    private const string SearchHomeOnlyKey = "search_home_only";
    private const string WebFallbackQuicklinkKey = "web_fallback_quicklink_id";
    private const string PreferredEditorKey = "preferred_editor";
    private const string HeyOpenTargetKey = "hey_open_target";
    private const string GraphicalEditorKey = "graphical_editor";
    private const string TerminalEditorKey = "terminal_editor";
    private const string SourcePreferencesRevisionKey = "source_preferences_revision";

    public async ValueTask<LauncherPreferences> GetPreferencesAsync( CancellationToken cancellationToken )
    {
        await InitializeAsync( cancellationToken )
            .ConfigureAwait( false );

        var defaults = LauncherPreferences.CreateDefault( );
        var registry = ContributionSnapshot;
        var sources = defaults.Sources.ToDictionary( static value => value.SourceId, StringComparer.Ordinal );

        foreach ( var source in registry.Sources )
        {
            sources.TryAdd( source.Id, DefaultSourcePreference( source, sources ) );
        }

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await ReadSourcePreferencesAsync( connection, sources, cancellationToken )
            .ConfigureAwait( false );

        var values = await ReadLauncherPreferenceValuesAsync( connection, defaults, cancellationToken )
            .ConfigureAwait( false );

        return BuildLauncherPreferences( registry.Revision, sources, values );
    }

    private static async ValueTask ReadSourcePreferencesAsync( SqliteConnection connection, Dictionary<string, SourcePreference> sources,
        CancellationToken cancellationToken )
    {
        await using ( var command = connection.CreateCommand( ) )
        {
            command.CommandText = """
                                  SELECT source_id, enabled_in_search, enabled_on_home, position
                                  FROM source_preferences;
                                  """;

            await using var reader = await command
                .ExecuteReaderAsync( cancellationToken )
                .ConfigureAwait( false );

            while ( await reader
                       .ReadAsync( cancellationToken )
                       .ConfigureAwait( false ) )
            {
                var sourceId = reader.GetString( 0 );
                if ( !sources.ContainsKey( sourceId ) )
                {
                    continue;
                }

                sources[sourceId] = ReadSourcePreference( sourceId, reader );
            }
        }
    }

    private static async ValueTask<LauncherPreferenceValues> ReadLauncherPreferenceValuesAsync( SqliteConnection connection, LauncherPreferences defaults,
        CancellationToken cancellationToken )
    {
        LauncherPreferenceValues values = new( defaults );
        await using var command = connection.CreateCommand( );
        command.CommandText = "SELECT key, value FROM launcher_preferences;";

        await using var reader = await command
            .ExecuteReaderAsync( cancellationToken )
            .ConfigureAwait( false );

        while ( await reader
                   .ReadAsync( cancellationToken )
                   .ConfigureAwait( false ) )
        {
            ReadLauncherPreferenceValue( values, reader.GetString( 0 ), reader.GetString( 1 ) );
        }

        return values;
    }

    private static LauncherPreferences BuildLauncherPreferences( long registryRevision, Dictionary<string, SourcePreference> sources,
        LauncherPreferenceValues values )
    {
        return new LauncherPreferences
        {
            RegistryRevision = registryRevision,
            Sources = OrderSourcePreferences( sources ),
            PreferenceRevision = values.PreferenceRevision,
            HideAfterAction = values.HideAfterAction,
            ClipboardHistoryShortcut = values.ClipboardHistoryShortcut,
            WorkspacePreviewMode = values.WorkspacePreviewMode,
            ReduceMotion = values.ReduceMotion,
            SearchHomeOnly = values.SearchHomeOnly,
            WebFallbackQuicklinkId = values.WebFallbackQuicklinkId,
            PreferredEditor = values.PreferredEditor,
            HeyOpenTarget = values.HeyOpenTarget,
            GraphicalEditor = EditorAdapterValidator.Validate( values.GraphicalEditor, terminal: false ),
            TerminalEditor = EditorAdapterValidator.Validate( values.TerminalEditor, terminal: true ),
            RememberSearches = ReadChoice( values.Choices.GetValueOrDefault( RememberSearchesKey ) ),
            LearnFromUse = ReadChoice( values.Choices.GetValueOrDefault( LearnFromUseKey ) ),
            OmarchyMenuState = ReadChoice( values.Choices.GetValueOrDefault( OmarchyMenuStateKey ) ),
            TopMatches = ReadChoice( values.Choices.GetValueOrDefault( TopMatchesKey ) ),
            InlineCalculator = ReadChoice( values.Choices.GetValueOrDefault( InlineCalculatorKey ) )
        };
    }

    private static List<SourcePreference> OrderSourcePreferences( Dictionary<string, SourcePreference> sources ) => sources
        .Values.OrderBy( static value => value.Position )
        .ThenBy( value => value.SourceId, StringComparer.Ordinal )
        .Select( ( source, index ) => PositionedSourcePreference( source, index ) )
        .ToList( );

    private static SourcePreference DefaultSourcePreference( SourceDescriptor source, Dictionary<string, SourcePreference> sources )
    {
        return new SourcePreference
        {
            SourceId = source.Id,
            EnabledInSearch = true,
            EnabledOnHome = false,
            Position = sources.Count + 1
        };
    }

    private static SourcePreference ReadSourcePreference( string sourceId, SqliteDataReader reader )
    {
        return new SourcePreference
        {
            SourceId = sourceId,
            EnabledInSearch = reader.GetInt64( 1 ) != 0,
            EnabledOnHome = reader.GetInt64( 2 ) != 0,
            Position = reader.GetInt32( 3 )
        };
    }

    private static EditorAdapter ReadEditor( string? value, EditorAdapter fallback )
    {
        if ( value is null )
        {
            return fallback;
        }

        return System.Text.Json.JsonSerializer.Deserialize( value, CommandDeckJsonContext.Default.EditorAdapter ) ?? fallback;
    }

    private static SourcePreference PositionedSourcePreference( SourcePreference source, int index )
    {
        return new SourcePreference
        {
            SourceId = source.SourceId,
            EnabledInSearch = source.EnabledInSearch,
            EnabledOnHome = source.EnabledOnHome,
            Position = index + 1
        };
    }

    private const string RememberSearchesKey = "remember_searches";
    private const string LearnFromUseKey = "learn_from_use";
    private const string OmarchyMenuStateKey = "omarchy_menu_state";
    private const string TopMatchesKey = "top_matches";
    private const string InlineCalculatorKey = "inline_calculator";

    // A choice made in Settings; a missing, empty or unknown value follows the configuration.
    private static bool? ReadChoice( string? value ) => value switch
    {
        "true" => true,
        "false" => false,
        _ => null
    };

    private static string WriteChoice( bool? value ) => value switch
    {
        true => "true",
        false => "false",
        null => string.Empty
    };

    public async ValueTask<PreferencesSnapshot> GetSnapshotAsync( bool includeShortcuts, CancellationToken cancellationToken )
    {
        var preferences = await GetPreferencesAsync( cancellationToken )
            .ConfigureAwait( false );

        var customizations = await ReadCustomizationsAsync( cancellationToken )
            .ConfigureAwait( false );

        var shortcuts = await InspectShortcutsAsync( includeShortcuts, cancellationToken )
            .ConfigureAwait( false );

        return CreatePreferencesSnapshot( preferences, customizations, shortcuts );
    }

    private static async ValueTask<IReadOnlyList<ShortcutBinding>> InspectShortcutsAsync( bool includeShortcuts, CancellationToken cancellationToken )
    {
        if ( includeShortcuts )
        {
            return await HyprlandShortcutInspector
                .InspectAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        return [];
    }

    private static PreferencesSnapshot CreatePreferencesSnapshot( LauncherPreferences preferences, List<CustomizationSummary> customizations,
        IReadOnlyList<ShortcutBinding> shortcuts )
    {
        return new PreferencesSnapshot
        {
            Preferences = preferences,
            Customizations = customizations,
            Shortcuts = [.. shortcuts],
            TotalCustomizationCount = customizations.Count
        };
    }

    public async ValueTask<LauncherPreferences> ReplacePreferencesAsync( LauncherPreferences preferences, CancellationToken cancellationToken )
    {
        ValidatePreferences( preferences );

        var now = _timeProvider
            .GetUtcNow( )
            .ToUnixTimeMilliseconds( );

        await InitializeAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var connection = await OpenAsync( cancellationToken )
            .ConfigureAwait( false );

        await using var transaction = ( SqliteTransaction ) await connection
            .BeginTransactionAsync( cancellationToken )
            .ConfigureAwait( false );

        await ValidateFallbackIfConfiguredAsync( connection, transaction, preferences, cancellationToken )
            .ConfigureAwait( false );

        var clipboardShortcut = ClipboardShortcut.Parse( preferences.ClipboardHistoryShortcut );

        await EnsureClipboardShortcutAvailableAsync( connection, transaction, clipboardShortcut, cancellationToken )
            .ConfigureAwait( false );

        await UpsertLauncherPreferenceAsync( connection, transaction, ClipboardHistoryShortcutKey, clipboardShortcut, now, cancellationToken )
            .ConfigureAwait( false );

        await EnsureRegistryRevisionAsync( connection, transaction, preferences.RegistryRevision, cancellationToken )
            .ConfigureAwait( false );

        await ReplaceSourcePreferencesAsync( connection, transaction, preferences, now, cancellationToken )
            .ConfigureAwait( false );

        await SaveLauncherOptionsAsync( connection, transaction, preferences, now, cancellationToken )
            .ConfigureAwait( false );

        await IncrementSourcePreferenceRevisionAsync( connection, transaction, cancellationToken )
            .ConfigureAwait( false );

        await UpsertLauncherPreferenceAsync( connection, transaction, WorkspacePreviewModeKey,
                WorkspacePreviewModes.Normalize( preferences.WorkspacePreviewMode ), now, cancellationToken )
            .ConfigureAwait( false );

        await transaction
            .CommitAsync( cancellationToken )
            .ConfigureAwait( false );

        return await GetPreferencesAsync( cancellationToken )
            .ConfigureAwait( false );
    }

    private static async ValueTask ReplaceSourcePreferencesAsync( SqliteConnection connection, SqliteTransaction transaction, LauncherPreferences preferences,
        long now, CancellationToken cancellationToken )
    {
        if ( preferences.RegistryRevision > 0 )
        {
            await using var revision = connection.CreateCommand( );
            revision.Transaction = transaction;
            revision.CommandText = "SELECT value FROM launcher_preferences WHERE key = 'source_preferences_revision';";

            var stored = ( string? ) await revision
                             .ExecuteScalarAsync( cancellationToken )
                             .ConfigureAwait( false ) ??
                         "0";

            if ( stored != preferences.PreferenceRevision.ToString( System.Globalization.CultureInfo.InvariantCulture ) )
            {
                throw new InvalidOperationException( "Source preferences changed. Refresh Settings and try again." );
            }
        }

        foreach ( var source in preferences.Sources )
        {
            await using var insert = connection.CreateCommand( );
            insert.Transaction = transaction;

            insert.CommandText = """
                                 INSERT INTO source_preferences(
                                     source_id, enabled_in_search, enabled_on_home, position, updated_unix_ms)
                                 VALUES($source, $search, $home, $position, $updated)
                                 ON CONFLICT(source_id) DO UPDATE SET enabled_in_search = excluded.enabled_in_search,
                                     enabled_on_home = excluded.enabled_on_home, position = excluded.position, updated_unix_ms = excluded.updated_unix_ms;
                                 """;

            insert.Parameters.AddWithValue( "$source", source.SourceId );
            insert.Parameters.AddWithValue( "$search", source.EnabledInSearch ? 1 : 0 );
            insert.Parameters.AddWithValue( "$home", source.EnabledOnHome ? 1 : 0 );
            insert.Parameters.AddWithValue( "$position", source.Position );
            insert.Parameters.AddWithValue( "$updated", now );

            await insert
                .ExecuteNonQueryAsync( cancellationToken )
                .ConfigureAwait( false );
        }
    }

    private static async ValueTask SaveLauncherOptionsAsync( SqliteConnection connection, SqliteTransaction transaction, LauncherPreferences preferences,
        long now, CancellationToken cancellationToken )
    {
        await UpsertLauncherPreferenceAsync( connection, transaction, SearchHomeOnlyKey, preferences.SearchHomeOnly ? "true" : "false", now, cancellationToken )
            .ConfigureAwait( false );

        await UpsertLauncherPreferenceAsync( connection, transaction, HideAfterActionKey, preferences.HideAfterAction ? "true" : "false", now,
                cancellationToken )
            .ConfigureAwait( false );

        await UpsertLauncherPreferenceAsync( connection, transaction, ReduceMotionKey, preferences.ReduceMotion ? "true" : "false", now, cancellationToken )
            .ConfigureAwait( false );

        foreach ( var (key, value) in new[]
                 {
                     (RememberSearchesKey, preferences.RememberSearches), (LearnFromUseKey, preferences.LearnFromUse),
                     (OmarchyMenuStateKey, preferences.OmarchyMenuState), (TopMatchesKey, preferences.TopMatches),
                     (InlineCalculatorKey, preferences.InlineCalculator)
                 } )
        {
            await UpsertLauncherPreferenceAsync( connection, transaction, key, WriteChoice( value ), now, cancellationToken )
                .ConfigureAwait( false );
        }

        await UpsertLauncherPreferenceAsync( connection, transaction, WebFallbackQuicklinkKey, preferences.WebFallbackQuicklinkId ?? string.Empty, now,
                cancellationToken )
            .ConfigureAwait( false );

        await UpsertLauncherPreferenceAsync( connection, transaction, PreferredEditorKey, preferences.PreferredEditor, now, cancellationToken )
            .ConfigureAwait( false );

        await UpsertLauncherPreferenceAsync( connection, transaction, HeyOpenTargetKey, preferences.HeyOpenTarget, now, cancellationToken )
            .ConfigureAwait( false );

        await UpsertLauncherPreferenceAsync( connection, transaction, GraphicalEditorKey,
                System.Text.Json.JsonSerializer.Serialize( preferences.GraphicalEditor, CommandDeckJsonContext.Default.EditorAdapter ), now, cancellationToken )
            .ConfigureAwait( false );

        await UpsertLauncherPreferenceAsync( connection, transaction, TerminalEditorKey,
                System.Text.Json.JsonSerializer.Serialize( preferences.TerminalEditor, CommandDeckJsonContext.Default.EditorAdapter ), now, cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask ValidateFallbackIfConfiguredAsync( SqliteConnection connection, SqliteTransaction transaction, LauncherPreferences preferences,
        CancellationToken cancellationToken )
    {
        if ( preferences.WebFallbackQuicklinkId is null )
        {
            return;
        }

        if ( ValidateFallback is null )
        {
            return;
        }

        await ValidateFallback( connection, transaction, preferences.WebFallbackQuicklinkId, cancellationToken )
            .ConfigureAwait( false );
    }

    private static async ValueTask UpsertLauncherPreferenceAsync( SqliteConnection connection, SqliteTransaction transaction, string key, string value,
        long now, CancellationToken cancellationToken )
    {
        await using var command = connection.CreateCommand( );
        command.Transaction = transaction;

        command.CommandText = """
                              INSERT INTO launcher_preferences(key, value, updated_unix_ms)
                              VALUES($key, $value, $now)
                              ON CONFLICT(key) DO UPDATE SET
                                  value = excluded.value,
                                  updated_unix_ms = excluded.updated_unix_ms;
                              """;

        command.Parameters.AddWithValue( "$key", key );
        command.Parameters.AddWithValue( "$value", value );
        command.Parameters.AddWithValue( "$now", now );

        await command
            .ExecuteNonQueryAsync( cancellationToken )
            .ConfigureAwait( false );
    }
}

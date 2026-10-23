namespace CommandDeck;

internal sealed partial class SqliteLauncherPreferenceStore
{
    private sealed class LauncherPreferenceValues( LauncherPreferences defaults )
    {
        internal long PreferenceRevision { get; set; }
        internal bool HideAfterAction { get; set; } = true;
        internal string ClipboardHistoryShortcut { get; set; } = defaults.ClipboardHistoryShortcut;
        internal string WorkspacePreviewMode { get; set; } = defaults.WorkspacePreviewMode;
        internal bool ReduceMotion { get; set; }
        internal bool SearchHomeOnly { get; set; }
        internal string? WebFallbackQuicklinkId { get; set; }
        internal string PreferredEditor { get; set; } = defaults.PreferredEditor;
        internal string HeyOpenTarget { get; set; } = defaults.HeyOpenTarget;
        internal EditorAdapter GraphicalEditor { get; set; } = defaults.GraphicalEditor;
        internal EditorAdapter TerminalEditor { get; set; } = defaults.TerminalEditor;
        internal Dictionary<string, string> Choices { get; } = new( StringComparer.Ordinal );
    }

    private static void ReadLauncherPreferenceValue( LauncherPreferenceValues values, string key, string value )
    {
        switch ( key )
        {
            case HideAfterActionKey:
                values.HideAfterAction = value != "false";

                break;

            case ClipboardHistoryShortcutKey:
                values.ClipboardHistoryShortcut = ClipboardShortcut.Parse( value );

                break;

            case WorkspacePreviewModeKey:
                values.WorkspacePreviewMode = WorkspacePreviewModes.Normalize( value );

                break;

            case ReduceMotionKey:
                values.ReduceMotion = value == "true";

                break;

            case SearchHomeOnlyKey:
                values.SearchHomeOnly = value == "true";

                break;

            case WebFallbackQuicklinkKey:
                values.WebFallbackQuicklinkId = string.IsNullOrWhiteSpace( value ) ? null : value;

                break;

            case PreferredEditorKey:
                values.PreferredEditor = value;

                break;

            case HeyOpenTargetKey:
                values.HeyOpenTarget = HeyOpenTargets.Normalize( value );

                break;

            case SourcePreferencesRevisionKey:
                values.PreferenceRevision = long.Parse( value, System.Globalization.CultureInfo.InvariantCulture );

                break;

            default:
                ReadEditorOrChoicePreference( values, key, value );

                break;
        }
    }

    private static void ReadEditorOrChoicePreference( LauncherPreferenceValues values, string key, string value )
    {
        switch ( key )
        {
            case GraphicalEditorKey:
                values.GraphicalEditor = ReadEditor( value, values.GraphicalEditor );

                break;

            case TerminalEditorKey:
                values.TerminalEditor = ReadEditor( value, values.TerminalEditor );

                break;

            case RememberSearchesKey or LearnFromUseKey or OmarchyMenuStateKey or TopMatchesKey or InlineCalculatorKey:
                values.Choices[key] = value;

                break;
        }
    }
}

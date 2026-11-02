namespace CommandDeck;

/// <summary>
/// Stable IDs for the Settings cards the launcher can open. Each card in the
/// QML Settings panel carries its ID, and its search terms come from
/// <see cref="SettingsIndex"/>, so the launcher and Settings search agree.
/// </summary>
public static class SettingIds
{
    public const string FileSearch = "search.files";
    public const string SearchSources = "search.sources";
    public const string HeyMail = "search.hey";
    public const string SearchBehavior = "search.behavior";
    public const string IndexedFolders = "search.folders";
    public const string Quicklinks = "search.quicklinks";
    public const string AiFeatures = "ai.features";
    public const string AssistantAccount = "ai.assistant.account";
    public const string AssistantModel = "ai.assistant.model";
    public const string AssistantTrust = "ai.assistant.trust";
    public const string AssistantLimits = "ai.assistant.limits";
    public const string AssistantGrants = "ai.assistant.grants";
    public const string AssistantDiagnostics = "ai.assistant.diagnostics";
    public const string QuickAnswerProviders = "ai.quick.providers";
    public const string QuickAnswerSelection = "ai.quick.selection";
    public const string PromptTemplates = "ai.quick.templates";
    public const string Editors = "editors.editors";
    public const string FilterAliases = "filters.aliases";
    public const string SavedSearches = "filters.saved";
    public const string Shortcuts = "shortcuts.shortcuts";
    public const string DesktopShortcuts = "shortcuts.desktop";
    public const string InterfaceFont = "customization.font";
    public const string DisplaySize = "customization.display";
    public const string Layout = "customization.layout";
    public const string Closing = "customization.closing";
    public const string Colors = "customization.colors";
    public const string Transparency = "customization.transparency";
    public const string Favorites = "customization.favorites";
    public const string HiddenItems = "customization.hidden";
    public const string Data = "customization.data";
    public const string Reset = "customization.reset";
    public const string Diagnostics = "diagnostics.status";
}

// One Settings card: the page it lives on, its section of the AI page, and the
// words that find it.
public sealed record SettingEntry( string Id, string Page, string Section, string Title, string Description, string Keywords )
{
    public string Breadcrumb => Section switch
    {
        SettingsIndex.AssistantSection => $"{SettingsIndex.PageTitle( Page )} › Assistant",
        SettingsIndex.QuickAnswersSection => $"{SettingsIndex.PageTitle( Page )} › Quick answers",
        _ => SettingsIndex.PageTitle( Page )
    };
}

public static class SettingsIndex
{
    // The QML Settings page and AI section IDs.
    public const string SearchPage = "search";
    public const string AiPage = "ai";
    public const string EditorsPage = "editors";
    public const string FiltersPage = "filters";
    public const string ShortcutsPage = "shortcuts";
    public const string CustomizationPage = "customization";
    public const string DiagnosticsPage = "diagnostics";
    public const string AssistantSection = "assistant";
    public const string QuickAnswersSection = "quick";

    public static IReadOnlyList<SettingEntry> Entries { get; } =
    [
        new( SettingIds.FileSearch, SearchPage, "", "File search", "Limit file and configuration results to your home folder.",
            "search home folder only indexed files configuration" ),
        new( SettingIds.SearchSources, SearchPage, "", "Search sources", "Choose which sources answer searches and Home, and their order.",
            ("search sources extensions install packages applications files windows workspaces commands " +
                "quicklinks clipboard snippets home order drag reorder checkbox keyboard") ),
        new( SettingIds.HeyMail, SearchPage, "", "HEY mail", "How hey: and mail: results open: web, app, or terminal.",
            "hey mail email threads open web app terminal tui" ),
        new( SettingIds.SearchBehavior, SearchPage, "", "Behavior",
            "Inline calculator, Top Matches, web fallback, hiding after opening, and reduced motion.",
            ("search inline calculator arithmetic dates days weeks top matches ranking best grouped sections web " +
                "fallback hide after opening item close copying reduce motion animation workspace layout previews " +
                "snapshots") ),
        new( SettingIds.IndexedFolders, SearchPage, "", "Indexed folders", "Add folders to search or disable configuration folders.",
            "search indexed folders roots path add remove disable enable configuration missing folder" ),
        new( SettingIds.Quicklinks, SearchPage, "", "Quicklinks", "Add web searches or links; {query} is the search text.",
            "quicklinks web searches urls templates aliases icons add edit remove disable enable" ),
        new( SettingIds.AiFeatures, AiPage, "", "AI features", "One switch for the assistant and quick answers.",
            "ai features master switch disable all assistant quick answers direct providers templates off" ),
        new( SettingIds.AssistantAccount, AiPage, AssistantSection, "Account", "Sign in to Codex, Claude Code, Cursor, or Grok.",
            "assistant enable account sign-in status sign in out login logout authentication codex claude cursor grok backend" ),
        new( SettingIds.AssistantModel, AiPage, AssistantSection, "Model", "The assistant's model and reasoning effort.",
            "assistant codex claude cursor grok backend model reasoning effort refresh hidden" ),
        new( SettingIds.AssistantTrust, AiPage, AssistantSection, "Trust and context", "How much the assistant may do, and which folders it sees.",
            ("assistant codex trust level context policy approved folders ask every access web search " +
                "unrestricted permission sandbox") ),
        new( SettingIds.AssistantLimits, AiPage, AssistantSection, "Limits", "Conversation retention and turn timeout.",
            "assistant codex conversation retention turn timeout duration" ),
        new( SettingIds.AssistantGrants, AiPage, AssistantSection, "Grants", "Review or revoke remembered permissions.",
            "assistant codex grants revoke all remembered authority permissions" ),
        new( SettingIds.AssistantDiagnostics, AiPage, AssistantSection, "Diagnostics", "Assistant status, helper package, and sandbox warnings.",
            "assistant codex diagnostics status package helper sandbox warnings" ),
        new( SettingIds.QuickAnswerProviders, AiPage, QuickAnswersSection, "Providers", "Ollama and OpenAI-compatible endpoints for ai: questions.",
            ("ai quick answers providers direct enable ollama openai grok xai endpoint api key credential model " +
                "provider add edit remove test") ),
        new( SettingIds.QuickAnswerSelection, AiPage, QuickAnswersSection, "Selected provider and model", "Which provider and model answer ai: questions.",
            "ai providers model refresh selection save selected active" ),
        new( SettingIds.PromptTemplates, AiPage, QuickAnswersSection, "Prompt templates", "Reusable prompts for the direct providers.",
            "ai providers prompt templates system user input new edit delete" ),
        new( SettingIds.Editors, EditorsPage, "", "File opening", "How CommandDeck opens a file at a line.",
            "editors preferred graphical terminal executable arguments" ),
        new( SettingIds.FilterAliases, FiltersPage, "", "Filter aliases", "Short tokens that stand for a filter.",
            "filters aliases tokens add remove" ),
        new( SettingIds.SavedSearches, FiltersPage, "", "Saved searches", "Keep a query and filters under a name, shortcut, or Home row.",
            "filters saved searches name shortcut home run edit delete" ),
        new( SettingIds.Shortcuts, ShortcutsPage, "", "CommandDeck", "Record the launcher hotkey and Clipboard History shortcut.",
            "shortcuts keyboard hotkeys clipboard history clip paste" ),
        new( SettingIds.DesktopShortcuts, ShortcutsPage, "", "Desktop", "Record desktop shortcuts and restore your original bindings.",
            "shortcuts desktop hyprland keyboard hotkeys bindings chords conflicts key window launcher" ),
        new( SettingIds.InterfaceFont, CustomizationPage, "", "Appearance", "Use a sans typeface or the shell font for the interface.",
            "customization appearance interface font typeface monospace sans mono shell" ),
        new( SettingIds.DisplaySize, CustomizationPage, "", "Display size", "Scale text and controls together.",
            "customization display size scale zoom percent larger smaller screen match recommended previous" ),
        new( SettingIds.Layout, CustomizationPage, "", "Layout", "Panel width and height, compact rows, and the preview pane.",
            ("customization layout width height panel percent reset compact dense rows one line badges footer " +
                "shortcuts preview pane details") ),
        new( SettingIds.Closing, CustomizationPage, "", "Closing and reopening", "What Escape does and how a reopened launcher starts.",
            "customization escape close reopen keep last search home root timeout" ),
        new( SettingIds.Colors, CustomizationPage, "", "Colors", "Follow the Omarchy theme or use CommandDeck's own palette.",
            "customization colors colours theme omarchy commanddeck palette accent background text override" ),
        new( SettingIds.Transparency, CustomizationPage, "", "Transparency", "Let the wallpaper show through the panel.",
            "customization transparent background opacity amount wallpaper" ),
        new( SettingIds.Favorites, CustomizationPage, "", "Favorites and aliases", "Order or remove favorites and aliases.",
            "customization favorites aliases bookmarks order unavailable remove" ),
        new( SettingIds.HiddenItems, CustomizationPage, "", "Hidden items", "Results you chose not to show, and restoring them.",
            "customization hidden items hide restore don't show unavailable" ),
        new( SettingIds.Data, CustomizationPage, "", "Data", "Search history, learned ranking, and export.",
            "customization data query history remember searches learn ranking usage export backup privacy" ),
        new( SettingIds.Reset, CustomizationPage, "", "Reset", "Return sources, favorites, aliases, and grants to their defaults.",
            "customization reset defaults" ),
        new( SettingIds.Diagnostics, DiagnosticsPage, "", "Status and timings", "Version, socket, index, and recent search timings, ready to copy.",
            ("diagnostics debug info copy report bug version socket daemon process started index catalog database " +
                "search timings slow latency providers timeout") )
    ];

    public static SettingEntry? Find( string? id ) =>
        Entries.FirstOrDefault( entry => string.Equals( entry.Id, id, StringComparison.Ordinal ) );

    public static string PageTitle( string page ) => page switch
    {
        SearchPage => "Search",
        AiPage => "AI",
        EditorsPage => "Editors",
        FiltersPage => "Filters",
        ShortcutsPage => "Shortcuts",
        CustomizationPage => "Customization",
        DiagnosticsPage => "Diagnostics",
        _ => "Settings"
    };
}

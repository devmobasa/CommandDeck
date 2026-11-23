using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public static class HerdrTargetKinds
{
    public const string Workspace = "workspace";
    public const string Tab = "tab";

    // Spaces and tabs the Herdr desktop app (herdr-app) shows. They live in
    // herdr-core, not in the terminal app's server, so they route to the app.
    public const string AppSpace = "app-space";
    public const string AppTab = "app-tab";

    // A desktop agent's own pane: focusing it shows its tab and space.
    public const string AppPane = "app-pane";

    public static bool IsSpace( string? kind ) => kind is Workspace or AppSpace;

    public static bool IsTab( string? kind ) => kind is Tab or AppTab;

    public static bool IsApp( string? kind ) => kind is AppSpace or AppTab or AppPane;
}

// Keeps the two apps apart where only an ID would otherwise name a row. A
// browsed space's scope (its rows' herdrWorkspaceId metadata) is a terminal-app
// space's ID, or a desktop-app space's ID after "app-space:"; a desktop agent's
// row ID puts "app-pane:" before its pane ID. Terminal-app IDs that start with
// either prefix are never listed, so a scope or row ID always names one app.
public static class HerdrScopes
{
    public const string AppSpacePrefix = HerdrTargetKinds.AppSpace + ":";
    public const string AppPanePrefix = "app-pane:";

    public static string Of( bool app, string workspaceId ) => app ? AppSpacePrefix + workspaceId : workspaceId;

    public static bool TryParse( string? scope, out bool app, [NotNullWhen( true )] out string? workspaceId )
    {
        app = scope?.StartsWith( AppSpacePrefix, StringComparison.Ordinal ) == true;
        workspaceId = app ? scope![AppSpacePrefix.Length..] : scope;
        if ( HerdrClient.IsValidIdentifier( workspaceId ) && workspaceId is not null )
        {
            return true;
        }

        workspaceId = null;

        return false;
    }

    public static bool IsReserved( string terminalId ) =>
        terminalId.StartsWith( AppSpacePrefix, StringComparison.Ordinal ) || terminalId.StartsWith( AppPanePrefix, StringComparison.Ordinal );
}

public enum HerdrAvailability
{
    NotInstalled,
    Stopped,
    Available,
    Incompatible,
    Failed
}

public sealed class HerdrStatus
{
    public required HerdrAvailability Availability { get; init; }
    public string? Version { get; init; }
    public long? Protocol { get; init; }
    public string? SocketPath { get; init; }
    public required string Detail { get; init; }
}

// App is true for a space, tab, or agent the Herdr desktop app shows.
public sealed record HerdrWorkspace( string Id, long Number, string Label, bool Focused, long PaneCount, long TabCount, string AgentStatus )
{
    public bool App { get; init; }

    // The machine a desktop-app space runs on; null when it is local.
    public string? Machine { get; init; }
}

public sealed record HerdrTab( string Id, string WorkspaceId, long Number, string Label, bool Focused, long PaneCount, string AgentStatus )
{
    public bool App { get; init; }
}

public sealed class HerdrInventory
{
    public required IReadOnlyList<HerdrWorkspace> Workspaces { get; init; }
    public required IReadOnlyList<HerdrTab> Tabs { get; init; }
}

public sealed class HerdrFocusOutcome
{
    public required bool Succeeded { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }
}

namespace CommandDeck;

/// <summary>Result metadata the launcher reads, beyond presentation fields.</summary>
public static class ResultMetadataKeys
{
    public const string IsDirectory = "isDirectory";
    public const string Value = "value";
    public const string Section = "section";
    public const string Key = "key";
    public const string Line = "line";
    public const string Column = "column";
    public const string Source = "source";
    public const string MenuId = "menuId";
    public const string MenuRoot = "menuRoot";
    public const string SourceGroup = "sourceGroup";
    public const string ProviderId = "providerId";
    public const string Prompt = "prompt";

    public const string KeyLine = "keyLine";
    public const string KeyColumn = "keyColumn";
    public const string ValueLine = "valueLine";
    public const string ValueColumn = "valueColumn";
    public const string Assignment = "assignment";
    public const string NativeRoute = "nativeRoute";
    public const string QuicklinkId = "quicklinkId";

    public const string CatalogResultId = "catalogResultId";
    public const string ExactAlias = "exactAlias";

    // The text Tab puts in the search field for this row, instead of its title:
    // a completion row names the whole query with its value filled in.
    public const string CompletionText = "completionText";

    // Conditions evaluated for Omarchy menu rows.
    public const string OmarchyWhen = "when";
    public const string OmarchyCheckedCondition = "checked";

    // Omarchy menu rows: "true" when the row's `when` guard failed (the row is
    // dimmed and scores lower) or its `checked` condition holds (it shows ✓).
    public const string OmarchyUnavailable = "omarchyUnavailable";
    public const string OmarchyChecked = "omarchyChecked";

    // Omarchy menu rows: the menu an entry is listed in ("root" at the top);
    // on a submenu row, the menu it opens, its label path ("Style › Theme") and
    // how many entries it lists.
    public const string OmarchyParent = "omarchyParent";
    public const string OmarchyMenu = "omarchyMenu";
    public const string OmarchyMenuPath = "omarchyMenuPath";
    public const string OmarchyMenuItems = "omarchyMenuItems";

    // The file or folder a row stands for.
    public const string Path = "path";

    // Hyprland rows: a window's address and workspace, and a workspace row's
    // identity for its preview.
    public const string Address = "address";
    public const string Workspace = "workspace";
    public const string WorkspaceId = "workspaceId";
    public const string WorkspaceName = "workspaceName";
    public const string MonitorName = "monitorName";
    public const string WorkspaceKind = "workspaceKind";

    // Extension search rows: the scoped provider and the item's lease handle.
    public const string ExtensionSearchProvider = "extensionSearchProvider";
    public const string ExtensionSearchLease = "extensionSearchLease";

    /// <summary>
    /// An application's catalog payload. Its Launch action receives the same
    /// values as its arguments.
    /// </summary>
    public static class Application
    {
        public const string DesktopFile = "desktopFile";
        public const string DesktopId = "desktopId";
        public const string Exec = "exec";
        public const string Icon = "icon";
        public const string StartupWmClass = "startupWmClass";
        public const string MimeTypes = "mimeTypes";
        public const string Terminal = "terminal";
        public const string Name = "applicationName";
        public const string LaunchArguments = "launchArguments";
    }
}

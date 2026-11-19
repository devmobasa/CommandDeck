namespace CommandDeck;

public static class EnvironmentVariableNames
{
    public const string ExecutablePath = "PATH";
    public const string Home = "HOME";
    public const string Language = "LANG";
    public const string LocaleAll = "LC_ALL";
    public const string Terminal = "TERM";
    public const string NoColor = "NO_COLOR";
    public const string TemporaryDirectory = "TMPDIR";
    public const string DotnetRoot = "DOTNET_ROOT";
    public const string MiseDataDirectory = "MISE_DATA_DIR";
    public const string WaylandDisplay = "WAYLAND_DISPLAY";
    public const string DbusSessionBusAddress = "DBUS_SESSION_BUS_ADDRESS";
    public const string HyprlandInstanceSignature = "HYPRLAND_INSTANCE_SIGNATURE";
    public const string OmarchyPath = "OMARCHY_PATH";
    public const string GcServer = "DOTNET_gcServer";
    public const string GcHeapHardLimit = "DOTNET_GCHeapHardLimit";
    public const string GcRegionRange = "DOTNET_GCRegionRange";
    public const string GcRegionSize = "DOTNET_GCRegionSize";
    public const string MallocArenaMaximum = "MALLOC_ARENA_MAX";
    public const string TieredPgo = "DOTNET_TieredPGO";
    public const string EnableDiagnostics = "DOTNET_EnableDiagnostics";
    public const string CodexHome = "CODEX_HOME";
    public const string ClaudeConfigDirectory = "CLAUDE_CONFIG_DIR";

    // Where developer tools keep their caches and stores, when moved.
    public const string CargoHome = "CARGO_HOME";
    public const string RustupHome = "RUSTUP_HOME";
    public const string GradleUserHome = "GRADLE_USER_HOME";
    public const string GoPath = "GOPATH";
    public const string GoModuleCache = "GOMODCACHE";
    public const string NuGetPackages = "NUGET_PACKAGES";
    public const string NuGetHttpCachePath = "NUGET_HTTP_CACHE_PATH";
    public const string NuGetPluginsCachePath = "NUGET_PLUGINS_CACHE_PATH";
    public const string NpmCache = "npm_config_cache";
    public const string PnpmHome = "PNPM_HOME";
    public const string NvmDirectory = "NVM_DIR";
    public const string VoltaHome = "VOLTA_HOME";
    public const string PyenvRoot = "PYENV_ROOT";
    public const string UvPythonInstallDirectory = "UV_PYTHON_INSTALL_DIR";
    public const string AndroidHome = "ANDROID_HOME";
    public const string AndroidSdkRoot = "ANDROID_SDK_ROOT";
    public const string CondaPackageDirectories = "CONDA_PKGS_DIRS";
}

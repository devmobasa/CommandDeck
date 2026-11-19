namespace CommandDeck;

// Ids of the built-in exclusions. They are stored in preferences, so an id
// never changes meaning; a retired id is ignored when it is read back.
public static class BuiltInExclusionIds
{
    public const string Cache = "xdg-cache";
    public const string Trash = "trash";
    public const string TransientFiles = "transient-files";
    public const string AppCaches = "app-caches";
    public const string FlatpakAppCaches = "flatpak-app-caches";
    public const string SnapAppCaches = "snap-app-caches";
    public const string CacheDirectoryTags = "cache-directory-tags";
    public const string NpmCache = "npm-cache";
    public const string BunCache = "bun-cache";
    public const string YarnCache = "yarn-cache";
    public const string GradleCaches = "gradle-caches";
    public const string NuGetHttpCache = "nuget-http-cache";
    public const string NuGetPluginsCache = "nuget-plugins-cache";
    public const string DotnetRunfile = "dotnet-runfile";
    public const string DotnetTelemetry = "dotnet-telemetry";
    public const string AspireCache = "aspire-cache";
    public const string AspireLogs = "aspire-logs";
    public const string AspireDashboard = "aspire-dashboard";
    public const string MiseDownloads = "mise-downloads";
    public const string GhcupCache = "ghcup-cache";

    public const string CargoRegistry = "cargo-registry";
    public const string CargoGit = "cargo-git";
    public const string RustupToolchains = "rustup-toolchains";
    public const string RustupDownloads = "rustup-downloads";
    public const string NuGetPackages = "nuget-packages";
    public const string DotnetSdk = "dotnet-sdk";
    public const string DotnetPacks = "dotnet-packs";
    public const string DotnetShared = "dotnet-shared";
    public const string DotnetSdkManifests = "dotnet-sdk-manifests";
    public const string DotnetToolStore = "dotnet-tool-store";
    public const string AspireVersions = "aspire-versions";
    public const string MavenRepository = "maven-repository";
    public const string GradleWrapper = "gradle-wrapper";
    public const string GoModules = "go-modules";
    public const string PnpmStore = "pnpm-store";
    public const string NvmVersions = "nvm-versions";
    public const string VoltaTools = "volta-tools";
    public const string FnmVersions = "fnm-versions";
    public const string UvPython = "uv-python";
    public const string PyenvVersions = "pyenv-versions";
    public const string PipenvEnvironments = "pipenv-environments";
    public const string CondaPackages = "conda-packages";
    public const string SdkmanCandidates = "sdkman-candidates";
    public const string GhcupCompilers = "ghcup-compilers";
    public const string StackPrograms = "stack-programs";
    public const string StackSnapshots = "stack-snapshots";
    public const string JuliaPackages = "julia-packages";
    public const string JuliaArtifacts = "julia-artifacts";
    public const string AndroidSdk = "android-sdk";
    public const string NeovimLazy = "nvim-lazy";
    public const string NeovimMason = "nvim-mason";
    public const string MiseInstalls = "mise-installs";
    public const string MiseDotnet = "mise-dotnet";
    public const string VscodeExtensions = "vscode-extensions";
    public const string VscodeServer = "vscode-server";
    public const string CursorExtensions = "cursor-extensions";
    public const string JetBrainsData = "jetbrains-data";

    public const string CodexData = "codex-data";
    public const string ClaudeData = "claude-data";
    public const string ClaudeWorktrees = "claude-worktrees";

    public const string PodmanLayers = "podman-layers";
    public const string PodmanVolumes = "podman-volumes";
    public const string DockerData = "docker-data";

    public const string FirefoxProfiles = "firefox-profiles";
    public const string FirefoxConfigProfiles = "firefox-config-profiles";
    public const string ChromiumProfile = "chromium-profile";
    public const string ChromeProfile = "chrome-profile";
    public const string BraveProfile = "brave-profile";
    public const string SlackData = "slack-data";
    public const string SignalData = "signal-data";
    public const string DiscordData = "discord-data";
    public const string Flatpak = "flatpak";
    public const string Steam = "steam";
}

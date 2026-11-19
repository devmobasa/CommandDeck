namespace CommandDeck;

using Ids = BuiltInExclusionIds;
using Categories = IndexExclusionCategories;
using Variables = EnvironmentVariableNames;

// The built-in exclusions by category. Everything that is on by default holds
// files nobody looks for by name: caches, downloaded packages and toolchains,
// app and agent state. Suggestions that can hold your own files start off.
internal static class BuiltInExclusionCatalog
{
    // A Firefox profile folder keeps your own styles and preferences.
    private static readonly string[] FirefoxKeep = ["native-messaging-hosts", "firefox/*/chrome", "firefox/*/user.js"];

    // Chromium and Electron keep these inside each profile or app folder.
    private static readonly string[] ChromiumCacheFolders =
    [
        "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache", "Service Worker",
        "blob_storage", "IndexedDB", "Local Storage", "Session Storage", "WebStorage", "Crashpad", "component_crx_cache", "Safe Browsing",
        "OptimizationGuideModelsManifest", "hyphen-data", "shared_proto_db", "GCM Store"
    ];

    public static IReadOnlyList<BuiltInExclusion> Caches { get; } =
    [
        Folder( Ids.Cache, Categories.Caches, ExclusionBase.CacheHome, "", "Application caches" ),
        Folder( Ids.Trash, Categories.Caches, ExclusionBase.DataHome, "Trash", "Deleted files" ),
        new( Ids.TransientFiles, Categories.Caches, ExclusionBase.Home, "",
            "SQLite -wal, -shm and -journal files beside their database, LevelDB files, .pid files and Vim swap files", true )
        {
            Kind = BuiltInExclusionKind.TransientFiles
        },
        new( Ids.AppCaches, Categories.Caches, ExclusionBase.ConfigHome, "", "Browser and Electron app caches inside each app folder", true )
        {
            Kind = BuiltInExclusionKind.AppCaches,
            Names = ChromiumCacheFolders,
            Also = [Home( ".var/app" ), Home( "snap" )]
        },
        new( Ids.FlatpakAppCaches, Categories.Caches, ExclusionBase.Home, ".var/app", "Flatpak app caches", true )
        {
            Kind = BuiltInExclusionKind.AppCaches,
            Names = ["cache"],
            TopLevel = true
        },
        new( Ids.SnapAppCaches, Categories.Caches, ExclusionBase.Home, "snap", "Snap app caches", true )
        {
            Kind = BuiltInExclusionKind.AppCaches,
            Names = [".cache"]
        },
        new( Ids.CacheDirectoryTags, Categories.Caches, ExclusionBase.Home, "", "Folders a tool marked as a cache with a CACHEDIR.TAG file", true )
        {
            Kind = BuiltInExclusionKind.CacheDirectoryTags
        },
        Folder( Ids.NpmCache, Categories.Caches, ExclusionBase.Home, ".npm/_cacache", "npm download cache" ) with
        {
            Also = [Variable( Variables.NpmCache, "_cacache" )]
        },
        Folder( Ids.BunCache, Categories.Caches, ExclusionBase.Home, ".bun/install/cache", "Bun download cache" ),
        Folder( Ids.YarnCache, Categories.Caches, ExclusionBase.Home, ".yarn/berry/cache", "Yarn download cache" ),
        Folder( Ids.GradleCaches, Categories.Caches, ExclusionBase.Home, ".gradle/caches", "Gradle build cache" ) with
        {
            Also = [Variable( Variables.GradleUserHome, "caches" )]
        },
        Folder( Ids.NuGetHttpCache, Categories.Caches, ExclusionBase.DataHome, "NuGet/http-cache", "NuGet HTTP cache" ) with
        {
            Also = [Variable( Variables.NuGetHttpCachePath )]
        },
        Folder( Ids.NuGetPluginsCache, Categories.Caches, ExclusionBase.DataHome, "NuGet/plugin-cache", "NuGet credential plugin cache" ) with
        {
            Also = [Variable( Variables.NuGetPluginsCachePath )]
        },
        Folder( Ids.DotnetRunfile, Categories.Caches, ExclusionBase.DataHome, "dotnet/runfile", ".NET file-based app builds" ),
        Folder( Ids.DotnetTelemetry, Categories.Caches, ExclusionBase.Home, ".dotnet/TelemetryStorageService", ".NET telemetry queue" ),
        Folder( Ids.AspireCache, Categories.Caches, ExclusionBase.Home, ".aspire/cache", "Aspire CLI cache" ),
        Folder( Ids.AspireLogs, Categories.Caches, ExclusionBase.Home, ".aspire/logs", "Aspire CLI logs" ),
        Folder( Ids.AspireDashboard, Categories.Caches, ExclusionBase.Home, ".aspire/dashboard", "Aspire dashboard runs" ),
        Folder( Ids.MiseDownloads, Categories.Caches, ExclusionBase.DataHome, "mise/downloads", "mise downloads" ),
        Folder( Ids.GhcupCache, Categories.Caches, ExclusionBase.Home, ".ghcup/cache", "GHCup downloads" )
    ];

    public static IReadOnlyList<BuiltInExclusion> Packages { get; } =
    [
        Folder( Ids.CargoRegistry, Categories.Packages, ExclusionBase.Home, ".cargo/registry", "Rust crate sources" ) with
        {
            Also = [Variable( Variables.CargoHome, "registry" )]
        },
        Folder( Ids.CargoGit, Categories.Packages, ExclusionBase.Home, ".cargo/git", "Rust Git dependencies" ) with
        {
            Also = [Variable( Variables.CargoHome, "git" )]
        },
        Folder( Ids.RustupToolchains, Categories.Packages, ExclusionBase.Home, ".rustup/toolchains", "Rust toolchains" ) with
        {
            Also = [Variable( Variables.RustupHome, "toolchains" )]
        },
        Folder( Ids.RustupDownloads, Categories.Packages, ExclusionBase.Home, ".rustup/downloads", "Rust downloads" ) with
        {
            Also = [Variable( Variables.RustupHome, "downloads" )]
        },
        Folder( Ids.NuGetPackages, Categories.Packages, ExclusionBase.Home, ".nuget/packages", "NuGet packages" ) with
        {
            Also = [Variable( Variables.NuGetPackages )]
        },
        Folder( Ids.DotnetSdk, Categories.Packages, ExclusionBase.Home, ".dotnet/sdk", ".NET SDKs" ),
        Folder( Ids.DotnetPacks, Categories.Packages, ExclusionBase.Home, ".dotnet/packs", ".NET targeting packs" ),
        Folder( Ids.DotnetShared, Categories.Packages, ExclusionBase.Home, ".dotnet/shared", ".NET runtimes" ),
        Folder( Ids.DotnetSdkManifests, Categories.Packages, ExclusionBase.Home, ".dotnet/sdk-manifests", ".NET workload manifests" ),
        Folder( Ids.DotnetToolStore, Categories.Packages, ExclusionBase.Home, ".dotnet/tools/.store", ".NET global tool packages" ),
        Folder( Ids.AspireVersions, Categories.Packages, ExclusionBase.Home, ".aspire/versions", "Aspire CLI versions" ),
        Folder( Ids.MavenRepository, Categories.Packages, ExclusionBase.Home, ".m2/repository", "Maven repository" ),
        Folder( Ids.GradleWrapper, Categories.Packages, ExclusionBase.Home, ".gradle/wrapper/dists", "Gradle distributions" ) with
        {
            Also = [Variable( Variables.GradleUserHome, "wrapper/dists" )]
        },
        Folder( Ids.GoModules, Categories.Packages, ExclusionBase.Home, "go/pkg/mod", "Go module cache" ) with
        {
            Also = [Variable( Variables.GoModuleCache ), Variable( Variables.GoPath, "pkg/mod", ':' )]
        },
        Folder( Ids.PnpmStore, Categories.Packages, ExclusionBase.DataHome, "pnpm/store", "pnpm package store" ) with
        {
            Also = [Variable( Variables.PnpmHome, "store" )]
        },
        Folder( Ids.NvmVersions, Categories.Packages, ExclusionBase.Home, ".nvm/versions", "Node.js versions from nvm" ) with
        {
            Also = [Variable( Variables.NvmDirectory, "versions" ), ExclusionLocation.Under( ExclusionBase.ConfigHome, "nvm/versions" )]
        },
        Folder( Ids.VoltaTools, Categories.Packages, ExclusionBase.Home, ".volta/tools", "Node.js tools from Volta" ) with
        {
            Also = [Variable( Variables.VoltaHome, "tools" )]
        },
        Folder( Ids.FnmVersions, Categories.Packages, ExclusionBase.DataHome, "fnm/node-versions", "Node.js versions from fnm" ),
        Folder( Ids.UvPython, Categories.Packages, ExclusionBase.DataHome, "uv/python", "Python versions from uv" ) with
        {
            Also = [Variable( Variables.UvPythonInstallDirectory )]
        },
        Folder( Ids.PyenvVersions, Categories.Packages, ExclusionBase.Home, ".pyenv/versions", "Python versions from pyenv" ) with
        {
            Also = [Variable( Variables.PyenvRoot, "versions" )]
        },
        Folder( Ids.PipenvEnvironments, Categories.Packages, ExclusionBase.DataHome, "virtualenvs", "Pipenv environments" ),
        Folder( Ids.CondaPackages, Categories.Packages, ExclusionBase.Home, ".conda/pkgs", "Conda packages" ) with
        {
            Also = [Variable( Variables.CondaPackageDirectories, "", ',' ), Home( "miniconda3/pkgs" ), Home( "anaconda3/pkgs" ), Home( "miniforge3/pkgs" )]
        },
        Folder( Ids.SdkmanCandidates, Categories.Packages, ExclusionBase.Home, ".sdkman/candidates", "SDKMAN! installs" ),
        Folder( Ids.GhcupCompilers, Categories.Packages, ExclusionBase.Home, ".ghcup/ghc", "Haskell compilers from GHCup" ),
        Folder( Ids.StackPrograms, Categories.Packages, ExclusionBase.Home, ".stack/programs", "Haskell Stack compilers" ),
        Folder( Ids.StackSnapshots, Categories.Packages, ExclusionBase.Home, ".stack/snapshots", "Haskell Stack packages" ),
        Folder( Ids.JuliaPackages, Categories.Packages, ExclusionBase.Home, ".julia/packages", "Julia packages" ),
        Folder( Ids.JuliaArtifacts, Categories.Packages, ExclusionBase.Home, ".julia/artifacts", "Julia artifacts" ),
        Folder( Ids.AndroidSdk, Categories.Packages, ExclusionBase.Home, "Android/Sdk", "Android SDK" ) with
        {
            Also = [Variable( Variables.AndroidHome ), Variable( Variables.AndroidSdkRoot )]
        },
        Folder( Ids.NeovimLazy, Categories.Packages, ExclusionBase.DataHome, "nvim/lazy", "Neovim plugins" ),
        Folder( Ids.NeovimMason, Categories.Packages, ExclusionBase.DataHome, "nvim/mason", "Neovim language tools" ),
        Folder( Ids.MiseInstalls, Categories.Packages, ExclusionBase.DataHome, "mise/installs", "mise tool installs" ),
        Folder( Ids.MiseDotnet, Categories.Packages, ExclusionBase.DataHome, "mise/dotnet-root", "mise .NET installs" ),
        Folder( Ids.VscodeExtensions, Categories.Packages, ExclusionBase.Home, ".vscode/extensions", "VS Code extensions" ),
        Folder( Ids.VscodeServer, Categories.Packages, ExclusionBase.Home, ".vscode-server", "VS Code remote server" ),
        Folder( Ids.CursorExtensions, Categories.Packages, ExclusionBase.Home, ".cursor/extensions", "Cursor extensions" ),
        Folder( Ids.JetBrainsData, Categories.Packages, ExclusionBase.DataHome, "JetBrains", "JetBrains IDE plugins and data" )
    ];

    public static IReadOnlyList<BuiltInExclusion> Agents { get; } =
    [
        new( Ids.CodexData, Categories.Agents, ExclusionBase.CodexHome, "", "Codex sessions, history, caches and state", true )
        {
            Keep = ["config.toml", "AGENTS.md", "AGENTS.override.md", "hooks.json", "skills", "prompts", "rules", "agents"]
        },
        new( Ids.ClaudeData, Categories.Agents, ExclusionBase.ClaudeHome, "", "Claude Code transcripts, file history, caches and state", true )
        {
            Keep = ["CLAUDE.md", "settings.json", "settings.local.json", "keybindings.json", "skills", "agents", "commands", "output-styles", "hooks",
                "rules", "themes"]
        },
        new( Ids.ClaudeWorktrees, Categories.Agents, ExclusionBase.Home, "", "Claude Code worktrees inside projects", true )
        {
            Kind = BuiltInExclusionKind.NamedFolder,
            Names = [".claude", "worktrees"]
        }
    ];

    public static IReadOnlyList<BuiltInExclusion> Containers { get; } =
    [
        Folder( Ids.PodmanLayers, Categories.Containers, ExclusionBase.DataHome, "containers/storage/overlay", "Podman image layers" ),
        Folder( Ids.PodmanVolumes, Categories.Containers, ExclusionBase.DataHome, "containers/storage/volumes", "Podman volumes", on: false ),
        Folder( Ids.DockerData, Categories.Containers, ExclusionBase.DataHome, "docker", "Rootless Docker data", on: false )
    ];

    public static IReadOnlyList<BuiltInExclusion> Applications { get; } =
    [
        Folder( Ids.FirefoxProfiles, Categories.Applications, ExclusionBase.Home, ".mozilla", "Firefox profiles" ) with
        {
            Keep = FirefoxKeep,
            Also = [Home( ".var/app/org.mozilla.firefox/.mozilla" ), Home( "snap/firefox/common/.mozilla" )]
        },
        Folder( Ids.FirefoxConfigProfiles, Categories.Applications, ExclusionBase.ConfigHome, "mozilla", "Firefox profiles" ) with
        {
            Keep = FirefoxKeep,
            Also = [Home( ".var/app/org.mozilla.firefox/config/mozilla" )]
        },
        Folder( Ids.ChromiumProfile, Categories.Applications, ExclusionBase.ConfigHome, "chromium", "Chromium profile" ) with
        {
            Keep = ["NativeMessagingHosts"],
            Also = [Home( ".var/app/org.chromium.Chromium/config/chromium" ), Home( "snap/chromium/common/chromium" )]
        },
        Folder( Ids.ChromeProfile, Categories.Applications, ExclusionBase.ConfigHome, "google-chrome", "Chrome profile" ) with
        {
            Keep = ["NativeMessagingHosts"],
            Also = [Home( ".var/app/com.google.Chrome/config/google-chrome" )]
        },
        Folder( Ids.BraveProfile, Categories.Applications, ExclusionBase.ConfigHome, "BraveSoftware", "Brave profile" ) with
        {
            Keep = ["Brave-Browser/NativeMessagingHosts"],
            Also = [Home( ".var/app/com.brave.Browser/config/BraveSoftware" )]
        },
        Folder( Ids.SlackData, Categories.Applications, ExclusionBase.ConfigHome, "Slack", "Slack app data" ) with
        {
            Also = [Home( ".var/app/com.slack.Slack" ), Home( "snap/slack" )]
        },
        Folder( Ids.SignalData, Categories.Applications, ExclusionBase.ConfigHome, "Signal", "Signal app data" ) with
        {
            Also = [Home( ".var/app/org.signal.Signal" ), Home( "snap/signal-desktop" )]
        },
        Folder( Ids.DiscordData, Categories.Applications, ExclusionBase.ConfigHome, "discord", "Discord app data" ) with
        {
            Also = [Home( ".var/app/com.discordapp.Discord" ), Home( "snap/discord" )]
        },
        Folder( Ids.Flatpak, Categories.Applications, ExclusionBase.DataHome, "flatpak", "Flatpak apps and runtimes" ) with
        {
            Keep = ["overrides"]
        },
        Folder( Ids.Steam, Categories.Applications, ExclusionBase.DataHome, "Steam", "Steam library", on: false )
    ];

    private static BuiltInExclusion Folder( string id, string category, ExclusionBase root, string relativePath, string description, bool on = true ) =>
        new( id, category, root, relativePath, description, on );

    private static ExclusionLocation Home( string relativePath ) => ExclusionLocation.Under( ExclusionBase.Home, relativePath );

    private static ExclusionLocation Variable( string variable, string relativePath = "", char? separator = null ) =>
        ExclusionLocation.FromVariable( variable, relativePath, separator );
}

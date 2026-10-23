namespace CommandDeck;

public sealed class CommandDeckOptions
{
    public ClipboardHistoryOptions ClipboardHistory { get; init; } = new( );
    public List<string> ScriptDirectories { get; init; } = [.. OptionLimits.CommandDeck.ScriptDirectories.Default];
    public List<SnippetOptions> Snippets { get; init; } = [];
    public List<ProjectOptions> Projects { get; init; } = [];
    public StorageOptions Storage { get; init; } = new( );
    public SearchOptions Search { get; init; } = new( );
    public IndexingOptions Indexing { get; init; } = new( );
    public PrivacyOptions Privacy { get; init; } = new( );
    public UiOptions Ui { get; init; } = new( );
    public List<QuicklinkOptions> Quicklinks { get; init; } = [];
    public List<CustomCommandOptions> Commands { get; init; } = [];
    public AiOptions Ai { get; init; } = new( );
    public AgentOptions Agent { get; init; } = new( );

    public static CommandDeckOptions CreateDefault( ) => new( )
    {
        Quicklinks =
        [
            new QuicklinkOptions
            {
                Id = "arch-wiki",
                Title = "Search Arch Wiki",
                Alias = "aw",
                UrlTemplate = "https://wiki.archlinux.org/index.php?search=" + UrlTemplateTokens.Query,
                Icon = "archlinux"
            },
            new QuicklinkOptions
            {
                Id = "github",
                Title = "Search GitHub",
                Alias = "gh",
                UrlTemplate = "https://github.com/search?q=" + UrlTemplateTokens.Query,
                Icon = "github"
            }
        ]
    };
}

public sealed class StorageOptions
{
    public string DatabasePath { get; init; } = OptionLimits.Storage.DatabasePath.Default;
    public int BusyTimeoutMilliseconds { get; init; } = OptionLimits.Storage.BusyTimeoutMilliseconds.Default;
}

public sealed class SearchOptions
{
    public int DefaultLimit { get; init; } = OptionLimits.Search.DefaultLimit.Default;
    public int MaximumLimit { get; init; } = OptionLimits.Search.MaximumLimit.Default;
    public int ProviderTimeoutMilliseconds { get; init; } = OptionLimits.Search.ProviderTimeoutMilliseconds.Default;

    public bool LearnFromInvocations { get; init; } = OptionLimits.Search.LearnFromInvocations.Default;

    // Run the `when` and `checked` conditions of Omarchy menu rows a search
    // shows, to dim rows whose guard fails and mark checked ones.
    public bool OmarchyMenuState { get; init; } = OptionLimits.Search.OmarchyMenuState.Default;
    public CalculatorOptions Calculator { get; init; } = new( );
    public RankingOptions Ranking { get; init; } = new( );

    // Named web-search engines (U3). Each engine renders a result that opens
    // urlTemplate with the query percent-encoded, and its alias becomes a
    // prefix filter (e.g. `g: rust traits` routes to Google). Set
    // includeInGlobal to also surface an engine for a bare query; the shipped
    // defaults stay alias-first so they complement, rather than replace, the
    // configurable web fallback (WebFallbackQuicklinkId).
    public List<WebEngineOptions> WebEngines { get; init; } =
    [
        new WebEngineOptions
        {
            Id = "google",
            Name = "Google",
            Alias = "g",
            UrlTemplate = "https://www.google.com/search?q=" + UrlTemplateTokens.Query,
            IncludeInGlobal = false
        },
        new WebEngineOptions
        {
            Id = "duckduckgo",
            Name = "DuckDuckGo",
            Alias = "ddg",
            UrlTemplate = "https://duckduckgo.com/?q=" + UrlTemplateTokens.Query,
            IncludeInGlobal = false
        }
    ];
}

public sealed class CalculatorOptions
{
    public bool Inline { get; init; } = OptionLimits.Calculator.Inline.Default;
    public bool ExchangeRates { get; init; } = OptionLimits.Calculator.ExchangeRates.Default;

    // Deprecated compatibility option; accepted and ignored.
    public string? UseQalc { get; init; }
}

public sealed class RankingOptions
{
    public double TitleWeight { get; init; } = OptionLimits.Ranking.TitleWeight.Default;
    public double PathWeight { get; init; } = OptionLimits.Ranking.PathWeight.Default;
    public double MetadataWeight { get; init; } = OptionLimits.Ranking.MetadataWeight.Default;
    public double ContentWeight { get; init; } = OptionLimits.Ranking.ContentWeight.Default;
    public double ConfigWeight { get; init; } = OptionLimits.Ranking.ConfigWeight.Default;
    public Dictionary<string, double> SourceWeights { get; init; } = new( StringComparer.Ordinal );
    public int MaximumResultsPerSource { get; init; } = OptionLimits.Ranking.MaximumResultsPerSource.Default;
    public int FirstPageSize { get; init; } = OptionLimits.Ranking.FirstPageSize.Default;
    public int MinimumSourcesOnFirstPage { get; init; } = OptionLimits.Ranking.MinimumSourcesOnFirstPage.Default;
    public TopMatchesOptions TopMatches { get; init; } = new( );
    public AdaptiveRankingOptions Adaptive { get; init; } = new( );
    public TimeAwareRankingOptions TimeAware { get; init; } = new( );
    public ContextRankingOptions Context { get; init; } = new( );
}

public sealed class TopMatchesOptions
{
    public bool Enabled { get; init; } = OptionLimits.TopMatches.Enabled.Default;
}

public sealed class TimeAwareRankingOptions
{
    public bool Enabled { get; init; } = OptionLimits.TimeAwareRanking.Enabled.Default;
    public double ApplicationMultiplier { get; init; } = OptionLimits.TimeAwareRanking.ApplicationMultiplier.Default;
    public double FileMultiplier { get; init; } = OptionLimits.TimeAwareRanking.FileMultiplier.Default;
    public double WorkspaceMultiplier { get; init; } = OptionLimits.TimeAwareRanking.WorkspaceMultiplier.Default;
    public double ActionMultiplier { get; init; } = OptionLimits.TimeAwareRanking.ActionMultiplier.Default;
}

public sealed class ContextRankingOptions
{
    public bool Enabled { get; init; } = OptionLimits.ContextRanking.Enabled.Default;
    public double ActiveApplicationBoost { get; init; } = OptionLimits.ContextRanking.ActiveApplicationBoost.Default;
    public double ActiveWorkspaceBoost { get; init; } = OptionLimits.ContextRanking.ActiveWorkspaceBoost.Default;
    public double CurrentDirectoryBoost { get; init; } = OptionLimits.ContextRanking.CurrentDirectoryBoost.Default;
    public double SelectedProjectBoost { get; init; } = OptionLimits.ContextRanking.SelectedProjectBoost.Default;
}

public sealed class AdaptiveRankingOptions
{
    public bool Enabled { get; init; } = OptionLimits.AdaptiveRanking.Enabled.Default;
    public int RetentionDays { get; init; } = OptionLimits.AdaptiveRanking.RetentionDays.Default;
    public int MaximumEvents { get; init; } = OptionLimits.AdaptiveRanking.MaximumEvents.Default;
}

public sealed record IndexingOptions
{
    public List<IndexRootOptions> Roots { get; set; } =
    [
        new IndexRootOptions { Path = "~", IncludeHidden = true, IncludeContentInSearch = false },
        new IndexRootOptions { Path = "~/Documents", IncludeHidden = true },
        new IndexRootOptions { Path = "~/.config/omarchy", IncludeHidden = true },
        new IndexRootOptions { Path = "~/.config/hypr", IncludeHidden = true }
    ];

    public long MaximumTextFileBytes { get; init; } = OptionLimits.Indexing.MaximumTextFileBytes.Default;
    public long MaximumDocumentFileBytes { get; init; } = OptionLimits.Indexing.MaximumDocumentFileBytes.Default;
    public int MaximumExtractedBytes { get; init; } = OptionLimits.Indexing.MaximumExtractedBytes.Default;
    public bool IndexDesktopApplications { get; init; } = OptionLimits.Indexing.IndexDesktopApplications.Default;
    public bool IndexOmarchyCommands { get; init; } = OptionLimits.Indexing.IndexOmarchyCommands.Default;
    public bool EnablePdfExtraction { get; init; } = OptionLimits.Indexing.EnablePdfExtraction.Default;
    public bool EnablePandocExtraction { get; init; } = OptionLimits.Indexing.EnablePandocExtraction.Default;
    public int ExtractorTimeoutMilliseconds { get; init; } = OptionLimits.Indexing.ExtractorTimeoutMilliseconds.Default;
    public long ExtractorMemoryLimitBytes { get; init; } = OptionLimits.Indexing.ExtractorMemoryLimitBytes.Default;
    public int ExtractorStandardErrorBytes { get; init; } = OptionLimits.Indexing.ExtractorStandardErrorBytes.Default;
    public int MaximumConcurrentExtractors { get; init; } = OptionLimits.Indexing.MaximumConcurrentExtractors.Default;
    public bool EnableIncrementalIndexing { get; init; } = OptionLimits.Indexing.EnableIncrementalIndexing.Default;
    public int IncrementalDebounceMilliseconds { get; init; } = OptionLimits.Indexing.IncrementalDebounceMilliseconds.Default;
    public int IncrementalMaximumLatencyMilliseconds { get; init; } = OptionLimits.Indexing.IncrementalMaximumLatencyMilliseconds.Default;
    public int IncrementalEventBatchLimit { get; init; } = OptionLimits.Indexing.IncrementalEventBatchLimit.Default;
    public bool EnableScheduledMaintenance { get; init; } = OptionLimits.Indexing.EnableScheduledMaintenance.Default;
    public int MaintenanceIntervalMinutes { get; init; } = OptionLimits.Indexing.MaintenanceIntervalMinutes.Default;
}

public sealed record IndexRootOptions
{
    public required string Path { get; init; }
    public bool IncludeHidden { get; init; } = OptionLimits.IndexRoot.IncludeHidden.Default;
    public bool IncludeFileNameInSearch { get; init; } = OptionLimits.IndexRoot.IncludeFileNameInSearch.Default;
    public bool IncludeMetadataInSearch { get; init; } = OptionLimits.IndexRoot.IncludeMetadataInSearch.Default;
    public bool IncludeContentInSearch { get; init; } = OptionLimits.IndexRoot.IncludeContentInSearch.Default;
    public bool FollowSymbolicLinks { get; init; } = OptionLimits.IndexRoot.FollowSymbolicLinks.Default;
    public bool ExcludeGeneratedDirectories { get; init; } = OptionLimits.IndexRoot.ExcludeGeneratedDirectories.Default;
    public bool RespectGitIgnore { get; init; } = OptionLimits.IndexRoot.RespectGitIgnore.Default;
    public List<string> IncludeGlobs { get; init; } = [];
    public List<string> ExcludeGlobs { get; init; } = [];
    public List<FileTypeIndexPolicyOptions> FileTypePolicies { get; init; } = [];
}

public sealed class FileTypeIndexPolicyOptions
{
    public required string Pattern { get; init; }
    public bool? IncludeFileNameInSearch { get; init; }
    public bool? IncludeMetadataInSearch { get; init; }
    public bool? IncludeContentInSearch { get; init; }
    public long? MaximumFileBytes { get; init; }
    public int? MaximumExtractedBytes { get; init; }
    public int? ExtractorTimeoutMilliseconds { get; init; }
    public long? ExtractorMemoryLimitBytes { get; init; }
}

public sealed class PrivacyOptions
{
    public bool StoreQueryHistory { get; init; } = OptionLimits.Privacy.StoreQueryHistory.Default;
    public int QueryHistoryRetentionDays { get; init; } = OptionLimits.Privacy.QueryHistoryRetentionDays.Default;
    public int MaximumQueryHistoryEntries { get; init; } = OptionLimits.Privacy.MaximumQueryHistoryEntries.Default;
    public bool StoreUsageHistory { get; init; } = OptionLimits.Privacy.StoreUsageHistory.Default;
    public bool PersistClipboard { get; init; } = OptionLimits.Privacy.PersistClipboard.Default;
    public List<string> NeverIndex { get; init; } = [.. OptionLimits.Privacy.NeverIndex.Default];
}

public sealed class UiOptions
{
    public string PreferredBinding { get; init; } = OptionLimits.Ui.PreferredBinding.Default;
    public bool AlsoBindSuperSpace { get; init; } = OptionLimits.Ui.AlsoBindSuperSpace.Default;
    public string MonitorPolicy { get; init; } = OptionLimits.Ui.MonitorPolicy.Default;
    public int DebounceMilliseconds { get; init; } = OptionLimits.Ui.DebounceMilliseconds.Default;

    /// <summary>Not used by the launcher; use Settings > Customization > Layout.</summary>
    public int Width { get; init; } = OptionLimits.Ui.Width.Default;

    /// <summary>Not used by the launcher; use Settings > Customization > Layout.</summary>
    public int MaximumVisibleRows { get; init; } = OptionLimits.Ui.MaximumVisibleRows.Default;
}

public sealed class QuicklinkOptions
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string UrlTemplate { get; init; }
    public string? Alias { get; init; }
    public string? Icon { get; init; }
}

public sealed class WebEngineOptions
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string UrlTemplate { get; init; }
    public required string Alias { get; init; }
    public bool IncludeInGlobal { get; init; } = OptionLimits.WebEngine.IncludeInGlobal.Default;
}

public sealed class CustomCommandOptions
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public List<string> Arguments { get; init; } = [];
    public List<string> Keywords { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public bool RequiresConfirmation { get; init; } = OptionLimits.CustomCommand.RequiresConfirmation.Default;
    public string? Icon { get; init; }
}

public sealed class AiOptions
{
    public bool Enabled { get; init; } = OptionLimits.Ai.Enabled.Default;

    public List<AiProviderOptions> Providers { get; init; } =
    [
        new AiProviderOptions
        {
            Id = AiProviderDefaults.OllamaId,
            DisplayName = AiProviderDefaults.OllamaDisplayName,
            Kind = AiAdapterKinds.Ollama,
            BaseUrl = AiProviderDefaults.OllamaBaseUrl,
            Model = "qwen3:8b"
        },
        new AiProviderOptions
        {
            Id = "cloud",
            DisplayName = AiProviderDefaults.OpenAiDisplayName,
            Kind = AiAdapterKinds.OpenAiChatCompletions,
            BaseUrl = AiProviderDefaults.OpenAiBaseUrl,
            Model = "gpt-5-mini",
            AllowRemoteEndpoint = true,
            Credential = new AiCredentialOptions { Kind = AiCredentialKinds.Environment, Name = OptionLimits.AiCredential.OpenAiApiKeyEnvironmentVariable },
            Enabled = false
        }
    ];
}

public sealed class AiProviderOptions
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public required string Kind { get; init; }
    public required string BaseUrl { get; init; }
    public required string Model { get; init; }
    public AiCredentialOptions Credential { get; init; } = new( );
    public bool AllowRemoteEndpoint { get; init; } = OptionLimits.AiProvider.AllowRemoteEndpoint.Default;
    public bool Enabled { get; init; } = OptionLimits.AiProvider.Enabled.Default;
    public long? ContextLimit { get; init; }
}

public static class AiCredentialKinds
{
    public const string None = "none";
    public const string Environment = "environment";
    public const string Systemd = "systemd";
}

public sealed class AiCredentialOptions
{
    public string Kind { get; init; } = OptionLimits.AiCredential.Kind.Default;
    public string? Name { get; init; }
}

public sealed class AgentOptions
{
    public string? CodexBinaryPath { get; init; }

    // Optional absolute overrides for the CLI agent backends; PATH discovery is
    // the default. Each CLI signs into a CommandDeck-owned home under
    // stateDirectory, never the user's own ~/.claude, ~/.cursor, or ~/.grok.
    public string? ClaudeBinaryPath { get; init; }
    public string? CursorBinaryPath { get; init; }
    public string? GrokBinaryPath { get; init; }
    public string BubblewrapPath { get; init; } = OptionLimits.Agent.BubblewrapPath.Default;
    public string SystemdRunPath { get; init; } = OptionLimits.Agent.SystemdRunPath.Default;
    public string RuntimeDirectory { get; init; } = OptionLimits.Agent.RuntimeDirectory.Default;

    public string StateDirectory { get; init; } = OptionLimits.Agent.StateDirectory.Default;

    // 0.152.1 is the reviewed version (CodexAppServerContract.ReviewedVersion).
    // The floor rejects CLIs that predate the App Server protocol; the ceiling
    // spans the rest of the 0.x line so routine Codex updates run rather than
    // hard-failing. Versions above the reviewed one are admitted but marked
    // unreviewed (status and Doctor say so). A 1.0 major bump falls outside the
    // range and re-trips the guard for a fresh schema review.
    public int MinimumCodexMajorVersion { get; init; } = OptionLimits.Agent.MinimumCodexMajorVersion.Default;
    public int MinimumCodexMinorVersion { get; init; } = OptionLimits.Agent.MinimumCodexMinorVersion.Default;
    public int MinimumCodexPatchVersion { get; init; } = OptionLimits.Agent.MinimumCodexPatchVersion.Default;
    public int MaximumCodexMajorVersion { get; init; } = OptionLimits.Agent.MaximumCodexMajorVersion.Default;
    public int MaximumCodexMinorVersion { get; init; } = OptionLimits.Agent.MaximumCodexMinorVersion.Default;
    public int MaximumCodexPatchVersion { get; init; } = OptionLimits.Agent.MaximumCodexPatchVersion.Default;

    public AiCredentialOptions ApiKeyCredential { get; init; } =
        new( ) { Kind = AiCredentialKinds.Environment, Name = OptionLimits.AiCredential.OpenAiApiKeyEnvironmentVariable };

    // Off by default, so every command the assistant runs is confirmed. When on, a
    // direct invocation of a known read-only tool (AgentLauncherTools.ClassifyCommandRisk)
    // runs without a per-command card; shells and every other command still confirm.
    public bool AutoApproveReadOnlyCommands { get; init; } = OptionLimits.Agent.AutoApproveReadOnlyCommands.Default;
}

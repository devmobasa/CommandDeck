namespace CommandDeck;

public sealed partial class CommandDeckRuntime
{
    private const int HealthWorkItemLimit = 50;
    private const string PdfExtractorHealthId = "extractor-pdf";
    private const string PandocExtractorHealthId = "extractor-pandoc";
    private const string ClipboardCopyHealthId = "clipboard-copy";

    public async ValueTask<RuntimeStatusSnapshot> GetRuntimeStatusAsync( CancellationToken cancellationToken )
    {
        var inspection = await _catalogOperations
            .InspectAsync( 0, HealthWorkItemLimit, cancellationToken )
            .ConfigureAwait( false );

        var catalog = inspection.Stats;

        var indexing = await _indexer
            .GetIncrementalStatusAsync( cancellationToken )
            .ConfigureAwait( false );

        List<ProviderHealthStatus> providers =
        [
            new ProviderHealthStatus( )
            {
                Id = ProviderIds.Catalog,
                Label = "Catalog",
                State = RuntimeHealthStates.Available,
                Detail = $"{catalog.ItemCount} indexed items"
            },
            CreateToolHealth( ToolExecutables.Hyprctl, ProviderIds.Hyprland, "Windows & Workspaces", required: true ),
            await CreateHerdrHealthAsync( cancellationToken )
                .ConfigureAwait( false ),
            new( )
            {
                Id = ProviderIds.Ai,
                Label = "AI",
                State = GetAiHealthState( ),
                Detail = EffectiveAiSettings.Error is { } configurationError ? configurationError.Summary : GetAiHealthDetail( )
            }
        ];

        AddOptionalToolHealth( providers, ToolExecutables.PdfToText, PdfExtractorHealthId, "PDF extraction", _options.Indexing.EnablePdfExtraction );
        AddOptionalToolHealth( providers, ToolExecutables.Pandoc, PandocExtractorHealthId, "Pandoc extraction", _options.Indexing.EnablePandocExtraction );
        AddOptionalToolHealth( providers, ToolExecutables.ClipboardCopy, ClipboardCopyHealthId, "Clipboard copy", enabled: true );

        return new RuntimeStatusSnapshot
        {
            Catalog = catalog,
            Indexing = indexing,
            Inspection = inspection,
            Providers = providers
        };
    }

    // Herdr is optional, so absence and a stopped server are inactive rather
    // than unavailable: the launcher banners only unavailable and degraded.
    private async ValueTask<ProviderHealthStatus> CreateHerdrHealthAsync( CancellationToken cancellationToken )
    {
        var preferences = await _preferences
            .GetPreferencesAsync( cancellationToken )
            .ConfigureAwait( false );

        var enabled = preferences.Sources.Any( static source => (source is { SourceId: SourceIds.Herdr, EnabledInSearch: true }) );
        if ( !enabled )
        {
            return new ProviderHealthStatus
            {
                Id = ProviderIds.Herdr,
                Label = "Herdr",
                State = RuntimeHealthStates.Disabled,
                Detail = "Disabled in Search sources."
            };
        }

        var status = await _herdr
            .GetStatusAsync( cancellationToken )
            .ConfigureAwait( false );

        var state = status.Availability switch
        {
            HerdrAvailability.Available => RuntimeHealthStates.Available,
            HerdrAvailability.NotInstalled or HerdrAvailability.Stopped => RuntimeHealthStates.Inactive,
            _ => RuntimeHealthStates.Degraded
        };

        var remediation = status.Availability switch
        {
            HerdrAvailability.NotInstalled =>
                "Install or update Herdr, then check the CommandDeck user-service PATH; the PATH-selected client is authoritative.",
            HerdrAvailability.Stopped => "Run herdr to start or attach the default session.",
            HerdrAvailability.Incompatible => "Update Herdr so the client on the CommandDeck PATH matches the running server, then restart the server.",
            HerdrAvailability.Failed => "Run `herdr --session default status server --json` to inspect the default session.",
            _ => null
        };

        return new ProviderHealthStatus
        {
            Id = ProviderIds.Herdr,
            Label = "Herdr",
            State = state,
            Detail = status.Detail,
            Remediation = remediation
        };
    }

    private static void AddOptionalToolHealth( List<ProviderHealthStatus> providers, string command, string id, string label, bool enabled )
    {
        if ( !enabled )
        {
            return;
        }

        providers.Add( CreateToolHealth( command, id, label, required: false ) );
    }

    private static ProviderHealthStatus CreateToolHealth( string command, string id, string label, bool required )
    {
        var found = ProcessRunner.ExistsOnPath( command );
        OptionalToolCatalog.TryGet( command, out var tool );
        var install = tool is null ? null : $"Install with: {tool.InstallCommand}";

        if ( found )
        {
            return new ProviderHealthStatus
            {
                Id = id,
                Label = label,
                State = RuntimeHealthStates.Available,
                Detail = "found on PATH"
            };
        }

        return new ProviderHealthStatus
        {
            Id = id,
            Label = label,
            State = required ? RuntimeHealthStates.Unavailable : RuntimeHealthStates.Degraded,
            Detail = $"{command} is not on PATH.",
            Remediation = DescribeMissingToolRemediation( required, install )
        };
    }

    private static string? DescribeMissingToolRemediation( bool required, string? install )
    {
        if ( !required )
        {
            return install;
        }

        var steps = new[] { install, "Then check the user service PATH and restart CommandDeck." }.Where( static value => !string.IsNullOrWhiteSpace( value ) );

        return string.Join( " ", steps );
    }

    private string GetAiHealthState( )
    {
        if ( EffectiveAiSettings.Enabled && EffectiveAiOptions.Providers.Count > 0 )
        {
            return RuntimeHealthStates.Available;
        }
        else
        {
            return RuntimeHealthStates.Disabled;
        }
    }

    private string GetAiHealthDetail( )
    {
        if ( EffectiveAiSettings.Enabled && EffectiveAiOptions.Providers.Count > 0 )
        {
            return $"{EffectiveAiOptions.Providers.Count} configured provider(s)";
        }
        else
        {
            return "AI is disabled until explicitly configured.";
        }
    }
}

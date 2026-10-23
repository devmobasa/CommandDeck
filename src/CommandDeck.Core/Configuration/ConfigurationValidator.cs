using System.Globalization;

namespace CommandDeck;

public static partial class ConfigurationValidator
{
    public static void Validate( CommandDeckOptions options )
    {
        ArgumentNullException.ThrowIfNull( options );

        if ( HasInvalidQueryHistoryLimits( options ) )
        {
            throw new InvalidOperationException(
                $"Query history retention must be {OptionLimits.Privacy.QueryHistoryRetentionDays.Minimum}-{OptionLimits.Privacy.QueryHistoryRetentionDays.Maximum} days with {OptionLimits.Privacy.MaximumQueryHistoryEntries.Minimum}-{OptionLimits.Privacy.MaximumQueryHistoryEntries.Maximum} entries." );
        }

        ValidateIndexing( options.Indexing );
        ValidateRanking( options.Search?.Ranking );
        ValidateCalculator( options.Search?.Calculator );
        ValidateAi( options.Ai );
        ValidateAgent( options.Agent );

        if ( options.Quicklinks is null )
        {
            throw new InvalidOperationException( "quicklinks must be an array." );
        }

        QuicklinkValidator.ValidateUnique( options.Quicklinks );
        ValidateWebEngines( options.Search?.WebEngines );
        ValidateProjects( options.Projects );

        if ( HasInvalidClipboardHistoryLimits( options ) )
        {
            throw new InvalidOperationException(
                $"Clipboard history requires {OptionLimits.ClipboardHistory.MaximumEntries.Minimum}–{OptionLimits.ClipboardHistory.MaximumEntries.Maximum} entries and {OptionLimits.ClipboardHistory.RetentionHours.Minimum}–{OptionLimits.ClipboardHistory.RetentionHours.Maximum} retention hours." );
        }

        if ( HasInvalidScriptDirectories( options ) )
        {
            throw new InvalidOperationException( $"Configure at most {OptionLimits.CommandDeck.ScriptDirectories.Maximum} nonempty script directories." );
        }

        SnippetTemplate.ValidateOptions( options.Snippets );
    }

    private static void ValidateProjects( List<ProjectOptions>? projects )
    {
        if ( HasInvalidProjects( projects ) )
        {
            throw new InvalidOperationException(
                $"Projects require a title, absolute or ~/ path, and valid editor arguments (maximum {OptionLimits.CommandDeck.Projects.Maximum} projects)." );
        }
    }

    private static void ValidateWebEngines( List<WebEngineOptions>? engines )
    {
        if ( engines is null )
        {
            throw new InvalidOperationException( "search.webEngines must be an array." );
        }

        HashSet<string> ids = new( StringComparer.OrdinalIgnoreCase );
        HashSet<string> aliases = new( StringComparer.OrdinalIgnoreCase );
        foreach ( var engine in engines )
        {
            if ( engine is null )
            {
                throw new InvalidOperationException( "search.webEngines must contain valid objects." );
            }

            ValidateIdentifier( engine.Id, "Web engine ID" );

            if ( !ids.Add( engine.Id ) )
            {
                throw new InvalidOperationException( $"Web engine ID '{engine.Id}' is configured more than once." );
            }

            if ( HasInvalidEngineName( engine ) )
            {
                throw new InvalidOperationException(
                    $"Web engine '{engine.Id}' name must be {OptionLimits.WebEngine.Name.Minimum}-{OptionLimits.WebEngine.Name.Maximum} characters without controls." );
            }

            if ( HasInvalidEngineTemplate( engine ) )
            {
                throw new InvalidOperationException(
                    $"Web engine '{engine.Id}' urlTemplate must be {OptionLimits.WebEngine.UrlTemplate.Minimum}-{OptionLimits.WebEngine.UrlTemplate.Maximum} characters and contain the " +
                    "{query} placeholder." );
            }

            var alias = ValidateWebEngineAlias( engine.Id, engine.Alias );
            if ( !aliases.Add( alias ) )
            {
                throw new InvalidOperationException( $"Web engine alias '{alias}' is configured more than once." );
            }
        }
    }

    private static string ValidateWebEngineAlias( string engineId, string alias )
    {
        var value = NormalizeEngineAlias( alias );
        if ( IsInvalidEngineAlias( value ) )
        {
            throw new InvalidOperationException(
                $"Web engine '{engineId}' alias must use {OptionLimits.WebEngine.Alias.Minimum}-{OptionLimits.WebEngine.Alias.Maximum} ASCII letters, digits, dots, " +
                "underscores, or hyphens, and must start with a letter or digit." );
        }

        if ( SearchFilterRegistry.IsReserved( value ) )
        {
            throw new InvalidOperationException( $"Web engine alias '{value}' conflicts with built-in launcher syntax." );
        }

        return value.ToLowerInvariant( );
    }

    private static void ValidateCalculator( CalculatorOptions? calculator )
    {
        if ( calculator is null )
        {
            throw new InvalidOperationException( "search.calculator is required." );
        }
    }

    private static void ValidateRanking( RankingOptions? ranking )
    {
        if ( ranking is not { SourceWeights: not null } )
        {
            throw new InvalidOperationException( "search.ranking and sourceWeights are required." );
        }

        if ( HasMissingRankingOptions( ranking ) )
        {
            throw new InvalidOperationException( "Adaptive, Top Matches, time-aware and context ranking controls are required." );
        }

        (string Name, double Value, double Minimum, double Maximum)[] bounds = GetRankingBounds( ranking );

        foreach ( (var name, var value, var minimum, var maximum) in bounds )
        {
            RequireRankingRange( name, value, minimum, maximum );
        }

        foreach ( (var sourceId, var weight) in ranking.SourceWeights )
        {
            if ( string.IsNullOrWhiteSpace( sourceId ) )
            {
                throw new InvalidOperationException( "search.ranking.sourceWeights keys must name a source." );
            }

            RequireRankingRange( $"search.ranking.sourceWeights.{sourceId}", weight, OptionLimits.Ranking.SourceWeights.Minimum,
                OptionLimits.Ranking.SourceWeights.Maximum );
        }
    }

    private static void RequireRankingRange( string name, double value, double minimum, double maximum )
    {
        if ( IsOutsideRankingBounds( value, minimum, maximum ) )
        {
            throw new InvalidOperationException( string.Create( CultureInfo.InvariantCulture,
                $"{name} must be between {minimum} and {maximum}; it is {value}." ) );
        }
    }

    private static void ValidateIndexing( IndexingOptions indexing )
    {
        if ( indexing?.Roots is null )
        {
            throw new InvalidOperationException( "indexing.roots must be an array." );
        }

        foreach ( var root in indexing.Roots )
        {
            ValidateIndexRoot( root );
        }

        if ( HasInvalidIndexingLimits( indexing ) )
        {
            throw new InvalidOperationException( "Index extractor limits are outside supported bounds." );
        }
    }

    public static void ValidateIndexRoot( IndexRootOptions? root )
    {
        if ( HasInvalidIndexRootPath( root ) )
        {
            throw new InvalidOperationException( "indexing.roots must contain objects with a non-empty path." );
        }

        try
        {
            _ = IndexRootPaths.Normalize( root.Path );
        }
        catch ( ArgumentException )
        {
            throw new InvalidOperationException( "Configured root path is invalid." );
        }

        if ( HasInvalidIndexGlobs( root ) )
        {
            throw new InvalidOperationException(
                $"Inclusion and exclusion globs for root '{root.Path}' must be arrays of non-empty patterns without NUL characters." );
        }

        if ( root.FileTypePolicies is null )
        {
            throw new InvalidOperationException( $"File type policies for root '{root.Path}' must be an array." );
        }

        foreach ( var policy in root.FileTypePolicies )
        {
            if ( HasInvalidDirectoryPolicy( policy ) )
            {
                throw new InvalidOperationException( $"File type policies for root '{root.Path}' require a filename glob." );
            }

            ValidatePositiveLimit( policy.MaximumFileBytes, "maximumFileBytes", root.Path );
            ValidatePositiveLimit( policy.MaximumExtractedBytes, "maximumExtractedBytes", root.Path );
            ValidatePositiveLimit( policy.ExtractorTimeoutMilliseconds, "extractorTimeoutMilliseconds", root.Path );
            ValidatePositiveLimit( policy.ExtractorMemoryLimitBytes, "extractorMemoryLimitBytes", root.Path );
        }
    }

    private static void ValidatePositiveLimit( long? value, string name, string root )
    {
        if ( value is < OptionLimits.FileTypeIndexPolicy.MinimumLimit )
        {
            throw new InvalidOperationException( $"File type policy {name} for root '{root}' must be positive." );
        }
    }

    private static void ValidateIdentifier( string value, string label )
    {
        if ( IsInvalidIdentifier( value ) )
        {
            throw new InvalidOperationException(
                $"{label} must use {OptionLimits.Identifiers.MinimumCharacters}-{OptionLimits.Identifiers.MaximumCharacters} ASCII letters, digits, dots, underscores, or hyphens, " +
                "and must start with a letter or digit." );
        }
    }

    private static bool IsAsciiLetterOrDigit( char value ) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static void ValidateAgent( AgentOptions agent )
    {
        ArgumentNullException.ThrowIfNull( agent );

        ValidateAbsoluteOrNullPath( agent.CodexBinaryPath, "agent.codexBinaryPath" );
        ValidateAbsoluteOrNullPath( agent.ClaudeBinaryPath, "agent.claudeBinaryPath" );
        ValidateAbsoluteOrNullPath( agent.CursorBinaryPath, "agent.cursorBinaryPath" );
        ValidateAbsoluteOrNullPath( agent.GrokBinaryPath, "agent.grokBinaryPath" );
        ValidateAbsoluteOrNullPath( agent.BubblewrapPath, "agent.bubblewrapPath" );
        ValidateAbsoluteOrNullPath( agent.SystemdRunPath, "agent.systemdRunPath" );
        ValidateCredentialReference( agent.ApiKeyCredential, "agent.apiKeyCredential" );

        if ( HasMissingAgentDirectories( agent ) )
        {
            throw new InvalidOperationException( "agent.runtimeDirectory and agent.stateDirectory must not be empty." );
        }

        if ( HasNegativeCodexVersion( agent ) )
        {
            throw new InvalidOperationException( "agent Codex version parts must not be negative." );
        }

        Version minimum = new( agent.MinimumCodexMajorVersion, agent.MinimumCodexMinorVersion, agent.MinimumCodexPatchVersion );
        Version maximum = new( agent.MaximumCodexMajorVersion, agent.MaximumCodexMinorVersion, agent.MaximumCodexPatchVersion );
        if ( maximum < minimum )
        {
            throw new InvalidOperationException( "agent maximum Codex version must not precede its minimum." );
        }
    }

    private static void ValidateAbsoluteOrNullPath( string? value, string label )
    {
        if ( value is null )
        {
            return;
        }

        if ( IsInvalidAbsolutePath( value ) )
        {
            throw new InvalidOperationException( $"{label} must be an absolute path." );
        }
    }

    private static bool HasInvalidQueryHistoryLimits( CommandDeckOptions options )
    {
        return options.Privacy.QueryHistoryRetentionDays is < OptionLimits.Privacy.QueryHistoryRetentionDays.Minimum
                   or > OptionLimits.Privacy.QueryHistoryRetentionDays.Maximum ||
               options.Privacy.MaximumQueryHistoryEntries is < OptionLimits.Privacy.MaximumQueryHistoryEntries.Minimum
                   or > OptionLimits.Privacy.MaximumQueryHistoryEntries.Maximum;
    }

    private static bool HasInvalidClipboardHistoryLimits( CommandDeckOptions options )
    {
        return options.ClipboardHistory is null ||
               options.ClipboardHistory.MaximumEntries is < OptionLimits.ClipboardHistory.MaximumEntries.Minimum
                   or > OptionLimits.ClipboardHistory.MaximumEntries.Maximum ||
               options.ClipboardHistory.RetentionHours is < OptionLimits.ClipboardHistory.RetentionHours.Minimum
                   or > OptionLimits.ClipboardHistory.RetentionHours.Maximum;
    }

    private static bool HasInvalidScriptDirectories( CommandDeckOptions options )
    {
        return options.ScriptDirectories is null ||
               options.ScriptDirectories.Count > OptionLimits.CommandDeck.ScriptDirectories.Maximum ||
               options.ScriptDirectories.Any( string.IsNullOrWhiteSpace );
    }

    private static bool HasInvalidProjects( List<ProjectOptions>? projects )
    {
        return projects is null ||
               projects.Count > OptionLimits.CommandDeck.Projects.Maximum ||
               projects.Any( project =>
                   project is null ||
                   string.IsNullOrWhiteSpace( project.Title ) ||
                   string.IsNullOrWhiteSpace( project.Path ) ||
                   !(System.IO.Path.IsPathFullyQualified( project.Path ) || project.Path.StartsWith( "~/", StringComparison.Ordinal )) ||
                   project.Path.Any( char.IsControl ) ||
                   string.IsNullOrWhiteSpace( project.Editor ) ||
                   project.Editor.Any( char.IsControl ) ||
                   project.EditorArguments is null ||
                   project.EditorArguments.Any( argument => argument is null || argument.Any( char.IsControl ) ) );
    }

    private static bool HasInvalidEngineName( WebEngineOptions engine )
    {
        return string.IsNullOrWhiteSpace( engine.Name ) || engine.Name.Length > OptionLimits.WebEngine.Name.Maximum || engine.Name.Any( char.IsControl );
    }

    private static bool HasInvalidEngineTemplate( WebEngineOptions engine )
    {
        return string.IsNullOrWhiteSpace( engine.UrlTemplate ) ||
               engine.UrlTemplate.Length > OptionLimits.WebEngine.UrlTemplate.Maximum ||
               engine.UrlTemplate.Any( char.IsControl ) ||
               !engine.UrlTemplate.Contains( UrlTemplateTokens.Query, StringComparison.OrdinalIgnoreCase );
    }

    private static string NormalizeEngineAlias( string alias )
    {
        return (alias ?? string.Empty)
            .Trim( )
            .TrimEnd( ':' );
    }

    private static bool IsInvalidEngineAlias( string value )
    {
        return value.Length is < OptionLimits.WebEngine.Alias.Minimum or > OptionLimits.WebEngine.Alias.Maximum ||
               !IsAsciiLetterOrDigit( value[0] ) ||
               value.Any( static character => !IsAsciiLetterOrDigit( character ) && character is not '.' and not '_' and not '-' );
    }

    private static bool HasMissingRankingOptions( RankingOptions ranking )
    {
        return ranking.Adaptive is null || ranking.TimeAware is null || ranking.Context is null || ranking.TopMatches is null;
    }

    private static (string Name, double Value, double Minimum, double Maximum)[] GetRankingBounds( RankingOptions ranking )
    {
        return
        [
            ("search.ranking.titleWeight", ranking.TitleWeight, OptionLimits.Ranking.MinimumWeight, OptionLimits.Ranking.MaximumWeight),
            ("search.ranking.pathWeight", ranking.PathWeight, OptionLimits.Ranking.MinimumWeight, OptionLimits.Ranking.MaximumWeight),
            ("search.ranking.metadataWeight", ranking.MetadataWeight, OptionLimits.Ranking.MinimumWeight, OptionLimits.Ranking.MaximumWeight),
            ("search.ranking.contentWeight", ranking.ContentWeight, OptionLimits.Ranking.MinimumWeight, OptionLimits.Ranking.MaximumWeight),
            ("search.ranking.configWeight", ranking.ConfigWeight, OptionLimits.Ranking.MinimumWeight, OptionLimits.Ranking.MaximumWeight),
            ("search.ranking.maximumResultsPerSource", ranking.MaximumResultsPerSource, OptionLimits.Ranking.MaximumResultsPerSource.Minimum,
                OptionLimits.Ranking.MaximumResultsPerSource.Maximum),
            ("search.ranking.firstPageSize", ranking.FirstPageSize, OptionLimits.Ranking.FirstPageSize.Minimum, OptionLimits.Ranking.FirstPageSize.Maximum),
            ("search.ranking.minimumSourcesOnFirstPage", ranking.MinimumSourcesOnFirstPage, OptionLimits.Ranking.MinimumSourcesOnFirstPage.Minimum,
                ranking.FirstPageSize),
            ("search.ranking.adaptive.retentionDays", ranking.Adaptive.RetentionDays, OptionLimits.AdaptiveRanking.RetentionDays.Minimum,
                OptionLimits.AdaptiveRanking.RetentionDays.Maximum),
            ("search.ranking.adaptive.maximumEvents", ranking.Adaptive.MaximumEvents, OptionLimits.AdaptiveRanking.MaximumEvents.Minimum,
                OptionLimits.AdaptiveRanking.MaximumEvents.Maximum),
            ("search.ranking.timeAware.applicationMultiplier", ranking.TimeAware.ApplicationMultiplier, OptionLimits.TimeAwareRanking.MinimumMultiplier,
                OptionLimits.TimeAwareRanking.MaximumMultiplier),
            ("search.ranking.timeAware.fileMultiplier", ranking.TimeAware.FileMultiplier, OptionLimits.TimeAwareRanking.MinimumMultiplier,
                OptionLimits.TimeAwareRanking.MaximumMultiplier),
            ("search.ranking.timeAware.workspaceMultiplier", ranking.TimeAware.WorkspaceMultiplier, OptionLimits.TimeAwareRanking.MinimumMultiplier,
                OptionLimits.TimeAwareRanking.MaximumMultiplier),
            ("search.ranking.timeAware.actionMultiplier", ranking.TimeAware.ActionMultiplier, OptionLimits.TimeAwareRanking.MinimumMultiplier,
                OptionLimits.TimeAwareRanking.MaximumMultiplier),
            ("search.ranking.context.activeApplicationBoost", ranking.Context.ActiveApplicationBoost, OptionLimits.ContextRanking.MinimumBoost,
                OptionLimits.ContextRanking.MaximumBoost),
            ("search.ranking.context.activeWorkspaceBoost", ranking.Context.ActiveWorkspaceBoost, OptionLimits.ContextRanking.MinimumBoost,
                OptionLimits.ContextRanking.MaximumBoost),
            ("search.ranking.context.currentDirectoryBoost", ranking.Context.CurrentDirectoryBoost, OptionLimits.ContextRanking.MinimumBoost,
                OptionLimits.ContextRanking.MaximumBoost),
            ("search.ranking.context.selectedProjectBoost", ranking.Context.SelectedProjectBoost, OptionLimits.ContextRanking.MinimumBoost,
                OptionLimits.ContextRanking.MaximumBoost)
        ];
    }

    private static bool IsOutsideRankingBounds( double value, double minimum, double maximum )
    {
        return !double.IsFinite( value ) || value < minimum || value > maximum;
    }

    private static bool HasInvalidIndexingLimits( IndexingOptions indexing )
    {
        return
            indexing.MaximumExtractedBytes is < OptionLimits.Indexing.MaximumExtractedBytes.Minimum or > OptionLimits.Indexing.MaximumExtractedBytes.Maximum ||
            indexing.ExtractorTimeoutMilliseconds is < OptionLimits.Indexing.ExtractorTimeoutMilliseconds.Minimum
                or > OptionLimits.Indexing.ExtractorTimeoutMilliseconds.Maximum ||
            indexing.ExtractorMemoryLimitBytes is < OptionLimits.Indexing.ExtractorMemoryLimitBytes.Minimum
                or > OptionLimits.Indexing.ExtractorMemoryLimitBytes.Maximum ||
            indexing.ExtractorStandardErrorBytes is < OptionLimits.Indexing.ExtractorStandardErrorBytes.Minimum
                or > OptionLimits.Indexing.ExtractorStandardErrorBytes.Maximum ||
            indexing.MaximumConcurrentExtractors is < OptionLimits.Indexing.MaximumConcurrentExtractors.Minimum
                or > OptionLimits.Indexing.MaximumConcurrentExtractors.Maximum ||
            indexing.MaintenanceIntervalMinutes is < OptionLimits.Indexing.MaintenanceIntervalMinutes.Minimum
                or > OptionLimits.Indexing.MaintenanceIntervalMinutes.Maximum;
    }

    private static bool HasInvalidIndexRootPath( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] IndexRootOptions? root )
    {
        return root is null || string.IsNullOrWhiteSpace( root.Path );
    }

    private static bool HasInvalidIndexGlobs( IndexRootOptions root )
    {
        return root.IncludeGlobs is null ||
               root.ExcludeGlobs is null ||
               root
                   .IncludeGlobs.Concat( root.ExcludeGlobs )
                   .Any( static pattern => string.IsNullOrWhiteSpace( pattern ) || pattern.Contains( '\0' ) );
    }

    private static bool HasInvalidDirectoryPolicy( FileTypeIndexPolicyOptions? policy )
    {
        return policy is null ||
               string.IsNullOrWhiteSpace( policy.Pattern ) ||
               Path.IsPathRooted( policy.Pattern ) ||
               policy.Pattern.Contains( Path.DirectorySeparatorChar ) ||
               policy.Pattern.Contains( Path.AltDirectorySeparatorChar );
    }

    private static bool IsInvalidIdentifier( string value )
    {
        return string.IsNullOrEmpty( value ) ||
               value.Length > OptionLimits.Identifiers.MaximumCharacters ||
               !IsAsciiLetterOrDigit( value[0] ) ||
               value.Any( static character => !IsAsciiLetterOrDigit( character ) && character is not '.' and not '_' and not '-' );
    }

    private static bool HasMissingAgentDirectories( AgentOptions agent )
    {
        return string.IsNullOrWhiteSpace( agent.RuntimeDirectory ) || string.IsNullOrWhiteSpace( agent.StateDirectory );
    }

    private static bool HasNegativeCodexVersion( AgentOptions agent )
    {
        return agent is { MinimumCodexMajorVersion: < OptionLimits.Agent.MinimumVersionPart }
            or { MinimumCodexMinorVersion: < OptionLimits.Agent.MinimumVersionPart }
            or { MinimumCodexPatchVersion: < OptionLimits.Agent.MinimumVersionPart }
            or { MaximumCodexMajorVersion: < OptionLimits.Agent.MinimumVersionPart }
            or { MaximumCodexMinorVersion: < OptionLimits.Agent.MinimumVersionPart }
            or { MaximumCodexPatchVersion: < OptionLimits.Agent.MinimumVersionPart };
    }

    private static bool IsInvalidAbsolutePath( string value )
    {
        return string.IsNullOrWhiteSpace( value ) || !value.StartsWith( '/' ) || value.Contains( '\0', StringComparison.Ordinal );
    }
}

using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

/// <summary>Searches only accepted manifest metadata. It has no process, broker, or network dependency.</summary>
public sealed class ExtensionCommandProvider( IContributionRegistry registry, ILauncherSearchPreferences preferences ) : ICommandProvider
{
    private const string KeywordsMetadata = "keywords";
    private const string OpenActionId = "open";
    private const double PartialMatchScore = 0.65;

    public string Id => ProviderIds.Extensions;

    public async ValueTask<IReadOnlyList<SearchResult>> SearchAsync( ParsedQuery query, SearchRequest request, CancellationToken cancellationToken )
    {
        var snapshot = request.ContributionSnapshot ?? registry.Snapshot;
        List<SearchResult> results = [];

        if ( query.Terms.Count > 0 )
        {
            var alias = await preferences
                .FindAliasAsync( query.Terms[0], cancellationToken )
                .ConfigureAwait( false );

            if ( IsAvailableAlias( query, snapshot, alias ) )
            {
                results.Add( alias );
            }
        }

        foreach ( var command in snapshot.Commands )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            if ( ShouldSkipCommand( query, results, command ) )
            {
                continue;
            }

            results.Add( CreateCommandResult( query, command ) );
        }

        // Reserve candidates fairly across packages before the coordinator applies its final source budgets.
        return ReservePackageCandidates( request, results );
    }

    private static bool Matches( ExtensionCommandContribution command, ParsedQuery query )
    {
        var metadata = command.Description + " " + string.Join( ' ', command.Keywords );

        foreach ( var clause in query.Expression.Clauses )
        {
            var text = GetClauseText( command, metadata, clause );
            var found = text.Contains( clause.Text, StringComparison.OrdinalIgnoreCase );
            if ( found == clause.Excluded )
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAvailableAlias( ParsedQuery query, ContributionRegistrySnapshot snapshot, [NotNullWhen( true )] SearchResult? alias )
    {
        return alias is { Kind: ResultKinds.ExtensionCommand } &&
               snapshot.Commands.Any( command =>
                   command.Available && command.StableId == alias.Id && (!command.Development || query.IncludesFilter( command.SourceId )) );
    }

    private static bool ShouldSkipCommand( ParsedQuery query, List<SearchResult> results, ExtensionCommandContribution command )
    {
        return !command.Available ||
               (command.Development && !query.IncludesFilter( command.SourceId )) ||
               results.Any( result => result.Id == command.StableId ) ||
               !Matches( command, query );
    }

    private SearchResult CreateCommandResult( ParsedQuery query, ExtensionCommandContribution command )
    {
        return new SearchResult
        {
            Id = command.StableId,
            Kind = ResultKinds.ExtensionCommand,
            Title = command.Title,
            Subtitle = command.Description,
            SourceId = command.SourceId,
            ProviderId = Id,
            Icon = ExtensionIcons.Addon,
            CanCustomize = true,
            Actions = [new CommandAction { Id = OpenActionId, Title = "Open command", Kind = ActionKinds.ExtensionOpen }],
            Score = string.Equals( command.Title, query.Text, StringComparison.OrdinalIgnoreCase ) ? 1 : PartialMatchScore,
            Metadata = new( StringComparer.Ordinal ) { [KeywordsMetadata] = string.Join( ' ', command.Keywords ) }
        };
    }

    private static SearchResult[] ReservePackageCandidates( SearchRequest request, List<SearchResult> results )
    {
        return results
            .GroupBy( result => result.SourceId )
            .SelectMany( group => group
                .OrderByDescending( result => result.Score )
                .ThenBy( result => result.Title, StringComparer.OrdinalIgnoreCase )
                .Select( ( result, index ) => (Result: result, Position: index) ) )
            .OrderBy( value => value.Position )
            .ThenByDescending( value => value.Result.Score )
            .ThenBy( value => value.Result.Id, StringComparer.Ordinal )
            .Take( request.Limit )
            .Select( value => value.Result )
            .ToArray( );
    }

    private static string GetClauseText( ExtensionCommandContribution command, string metadata, QueryClause clause )
    {
        return clause.Field switch
        {
            null => command.Title + " " + metadata,
            QueryFieldIds.Title => command.Title,
            QueryFieldIds.Metadata => metadata,
            _ => ""
        };
    }
}

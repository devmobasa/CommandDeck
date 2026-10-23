using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace CommandDeck;

public interface IContributionRegistry
{
    ContributionRegistrySnapshot Snapshot { get; }
}

public sealed class ContributionRegistrySnapshot
{
    private readonly FrozenDictionary<string, FilterDescriptor> _filtersById;
    private readonly FrozenDictionary<string, FilterDescriptor> _filtersByToken;
    private readonly FrozenDictionary<string, SourceDescriptor> _sourcesById;

    public static ContributionRegistrySnapshot BuiltIns { get; } = new( 0, [], [], [] );

    public ContributionRegistrySnapshot( long revision, IEnumerable<SourceDescriptor> extensionSources, IEnumerable<FilterDescriptor> extensionFilters,
        IEnumerable<ExtensionCommandContribution> commands )
    {
        Revision = revision;
        Sources = MergeSources( extensionSources );
        Filters = MergeFilters( extensionFilters );
        Commands = Array.AsReadOnly( commands.ToArray( ) );
        _sourcesById = Sources.ToFrozenDictionary( source => source.Id, StringComparer.Ordinal );
        _filtersById = Filters.ToFrozenDictionary( filter => filter.Id, StringComparer.OrdinalIgnoreCase );
        _filtersByToken = IndexFilterTokens( );

        if ( HasCommandCollisions( ) )
        {
            throw new InvalidDataException( "Extension commands have colliding identities." );
        }
    }

    public long Revision { get; }
    public IReadOnlyList<SourceDescriptor> Sources { get; }
    public IReadOnlyList<FilterDescriptor> Filters { get; }
    public IReadOnlyList<ExtensionCommandContribution> Commands { get; }
    public FilterDescriptor? FindById( string? id ) => id is null ? null : _filtersById.GetValueOrDefault( id );
    public FilterDescriptor? FindByToken( string token ) => _filtersByToken.GetValueOrDefault( NormalizeToken( token ) );

    public string Label( string sourceId ) => _sourcesById.GetValueOrDefault( sourceId )
                                                  ?.Label ??
                                              LauncherSourceRegistry.Label( sourceId );

    public IReadOnlyList<FilterDescriptor> WithAliases( IReadOnlyList<FilterAlias> aliases ) => Filters
        .Select( filter => WithMatchingAliases( filter, aliases ) )
        .ToArray( );

    private static FilterDescriptor WithMatchingAliases( FilterDescriptor filter, IReadOnlyList<FilterAlias> aliases ) => new( )
    {
        Id = filter.Id,
        Label = filter.Label,
        Token = filter.Token,
        ProviderId = filter.ProviderId,
        SourceId = filter.SourceId,
        Kind = filter.Kind,
        Available = filter.Available,
        Aliases = [.. filter.Aliases, .. MatchingAliases( filter, aliases )]
    };

    private static IEnumerable<string> MatchingAliases( FilterDescriptor filter, IReadOnlyList<FilterAlias> aliases ) => aliases
        .Where( alias => alias.FilterId == filter.Id )
        .Select( alias => alias.Alias + ":" );

    private static ReadOnlyCollection<SourceDescriptor> MergeSources( IEnumerable<SourceDescriptor> extensionSources ) =>
        Array.AsReadOnly( LauncherSourceRegistry
            .Configurable.Concat( extensionSources )
            .ToArray( ) );

    private static ReadOnlyCollection<FilterDescriptor> MergeFilters( IEnumerable<FilterDescriptor> extensionFilters ) =>
        Array.AsReadOnly( SearchFilterRegistry
            .All.Concat( extensionFilters )
            .ToArray( ) );

    private FrozenDictionary<string, FilterDescriptor> IndexFilterTokens( ) => Filters
        .SelectMany( GetFilterTokens )
        .ToFrozenDictionary( value => value.Token, value => value.Filter, StringComparer.OrdinalIgnoreCase );

    private static IEnumerable<(string Token, FilterDescriptor Filter)> GetFilterTokens( FilterDescriptor filter ) => filter
        .Aliases.Prepend( filter.Token )
        .Select( token => (Token: token.TrimEnd( ':' ), Filter: filter) );

    private bool HasCommandCollisions( ) => Commands
                                                .Select( command => command.StableId )
                                                .Distinct( StringComparer.Ordinal )
                                                .Count( ) !=
                                            Commands.Count;

    private static string NormalizeToken( string token ) => token
        .Trim( )
        .TrimEnd( ':' );
}

public sealed record ExtensionCommandContribution(
    string StableId,
    string InstallationKey,
    string CommandId,
    string SourceId,
    string Title,
    string Description,
    IReadOnlyList<string> Keywords,
    bool Available,
    bool Development = false );

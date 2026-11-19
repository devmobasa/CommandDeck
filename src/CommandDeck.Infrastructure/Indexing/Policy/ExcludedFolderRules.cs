using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

internal sealed record ExcludedFolderRule( string Id, string Source, string LexicalPath, string? CanonicalPath )
{
    private const string AnyName = "*";

    // Paths inside the folder that stay indexed, where * stands for any one
    // name. The folder and each folder on the way to a kept path are entered,
    // and only their other entries are excluded.
    public IReadOnlyList<string> Keep { get; init; } = [];

    public bool IsUser => Source == IndexExclusionSources.User;

    public bool Contains( string path ) =>
        ExcludedFolderPaths.IsSameOrDescendant( LexicalPath, path )
        || (CanonicalPath is not null && ExcludedFolderPaths.IsSameOrDescendant( CanonicalPath, path ));

    // A built-in rule counts only when one path form places the rule strictly
    // inside the root and the path inside the rule, so an alias can never make
    // a built-in cover the whole root.
    public bool ContainsBelowRoot( string path, string lexicalRoot, string canonicalRoot, bool directory ) =>
        (IsBelowRootAndContains( LexicalPath, lexicalRoot, path ) && !IsKept( LexicalPath, path, directory ))
        || (CanonicalPath is not null && IsBelowRootAndContains( CanonicalPath, canonicalRoot, path ) && !IsKept( CanonicalPath, path, directory ));

    private bool IsKept( string folder, string path, bool directory )
    {
        if ( Keep.Count == 0 )
        {
            return false;
        }

        if ( path.Length == folder.Length )
        {
            return true;
        }

        var names = path[(folder.Length + 1)..].Split( '/' );

        return Keep.Any( kept => IsKeptBy( kept.Split( '/' ), names, directory ) );
    }

    // A kept path keeps everything below it. A folder on the way to one is
    // entered; a file there is not kept.
    private static bool IsKeptBy( string[] kept, string[] names, bool directory )
    {
        if ( names.Length < kept.Length && !directory )
        {
            return false;
        }

        for ( var index = 0; index < Math.Min( kept.Length, names.Length ); index++ )
        {
            if ( kept[index] != AnyName && !string.Equals( kept[index], names[index], StringComparison.Ordinal ) )
            {
                return false;
            }
        }

        return true;
    }

    // True when either form of the rule is the folder or lies on either side of it.
    public bool Overlaps( string folder ) =>
        IsRelated( LexicalPath, folder ) || (CanonicalPath is not null && IsRelated( CanonicalPath, folder ));

    private static bool IsBelowRootAndContains( string rule, string root, string path ) =>
        IsStrictlyBelow( root, rule ) && ExcludedFolderPaths.IsSameOrDescendant( rule, path );

    private static bool IsStrictlyBelow( string folder, string path ) =>
        path.Length > folder.Length && ExcludedFolderPaths.IsSameOrDescendant( folder, path );

    private static bool IsRelated( string rule, string folder ) =>
        ExcludedFolderPaths.IsSameOrDescendant( rule, folder ) || ExcludedFolderPaths.IsSameOrDescendant( folder, rule );
}

// An immutable set of excluded folders. User folders exclude everything below
// them, an indexed folder included. Built-in folders yield to an indexed folder
// at or below them, so a folder the user chose to index is always indexed.
public sealed class ExcludedFolderRules
{
    // A new version reconciles the index once, when what a rule matches changes.
    private const string FingerprintVersion = "excluded-folders-v3";

    private ExcludedFolderRules( IReadOnlyList<ExcludedFolderRule> rules, IReadOnlyList<ExcludedNameRule> names, IReadOnlyList<ExcludedScopeRule> scopes,
        string? transientFilesId, string? cacheDirectoryTagsId )
    {
        Rules = rules;
        NameRules = names;
        ScopeRules = scopes;
        TransientFilesId = transientFilesId;
        CacheDirectoryTagsId = cacheDirectoryTagsId;
        PatternKey = ComputePatternKey( names, scopes, transientFilesId, cacheDirectoryTagsId );
        Fingerprint = ComputeFingerprint( rules, PatternKey );
    }

    public static ExcludedFolderRules Empty { get; } = new( [], [], [], null, null );

    internal IReadOnlyList<ExcludedFolderRule> Rules { get; }
    internal IReadOnlyList<ExcludedNameRule> NameRules { get; }
    internal IReadOnlyList<ExcludedScopeRule> ScopeRules { get; }
    internal string? TransientFilesId { get; }

    // Set when folders tagged with CACHEDIR.TAG are skipped.
    internal string? CacheDirectoryTagsId { get; }

    // Name, scope, file and tag rules; a change to them needs a full purge.
    private string PatternKey { get; }

    public string Fingerprint { get; }

    public int Count => Rules.Count;

    public static ExcludedFolderRules Defaults( ) => Create( null, null );

    public static ExcludedFolderRules Create( IReadOnlyList<BuiltInExclusionOverride>? overrides, IReadOnlyList<ExcludedFolder>? userFolders )
    {
        var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
        List<ExcludedFolderRule> rules = [];
        List<ExcludedNameRule> names = [];
        List<ExcludedScopeRule> scopes = [];
        string? transientFilesId = null;
        string? cacheDirectoryTagsId = null;

        foreach ( var folder in userFolders ?? [] )
        {
            rules.Add( CreateRule( folder.Id, IndexExclusionSources.User, folder.Path ) );
        }

        foreach ( var exclusion in BuiltInExclusions.All.Where( exclusion => BuiltInExclusions.IsEnabled( exclusion, overrides ) ) )
        {
            switch ( exclusion.Kind )
            {
                case BuiltInExclusionKind.NamedFolder:
                    names.Add( new ExcludedNameRule( exclusion.Id, BuiltInExclusions.DescribeLocation( exclusion, home ) ?? "", exclusion.Names ) );

                    break;
                case BuiltInExclusionKind.TransientFiles:
                    transientFilesId = exclusion.Id;

                    break;
                case BuiltInExclusionKind.CacheDirectoryTags:
                    cacheDirectoryTagsId = exclusion.Id;

                    break;
                case BuiltInExclusionKind.AppCaches:
                    AddScopeRules( scopes, exclusion, home );

                    break;
                default:
                    AddFolderRules( rules, exclusion, home );

                    break;
            }
        }

        return new ExcludedFolderRules( rules, names, scopes, transientFilesId, cacheDirectoryTagsId );
    }

    // One rule for each place the folder can be.
    private static void AddFolderRules( List<ExcludedFolderRule> rules, BuiltInExclusion exclusion, string home )
    {
        foreach ( var path in ResolveAll( exclusion, home ) )
        {
            if ( rules.Any( rule => rule.LexicalPath == path ) )
            {
                continue;
            }

            var rule = CreateRule( exclusion.Id, IndexExclusionSources.BuiltIn, path ) with { Keep = exclusion.Keep };
            if ( !IsHomeOrAncestor( rule.CanonicalPath, home ) )
            {
                rules.Add( rule );
            }
        }
    }

    // A scope folder that resolves to home or above is skipped like a folder.
    private static void AddScopeRules( List<ExcludedScopeRule> scopes, BuiltInExclusion exclusion, string home )
    {
        var names = exclusion.Names.ToHashSet( StringComparer.Ordinal );

        foreach ( var scope in ResolveAll( exclusion, home ) )
        {
            var canonical = ResolveCanonical( scope );
            if ( !IsHomeOrAncestor( canonical, home ) )
            {
                var display = BuiltInExclusions.DescribeScope( exclusion, scope );
                scopes.Add( new ExcludedScopeRule( exclusion.Id, display, scope, canonical, names ) { TopLevel = exclusion.TopLevel } );
            }
        }
    }

    private static IReadOnlyList<string> ResolveAll( BuiltInExclusion exclusion, string home ) =>
        string.IsNullOrEmpty( home ) ? [] : BuiltInExclusions.ResolveAll( exclusion, home );

    // A built-in folder that resolves to home or above, in either form of home,
    // would hide everything.
    private static bool IsHomeOrAncestor( string? canonical, string home )
    {
        if ( canonical is null )
        {
            return false;
        }

        var lexicalHome = Path.TrimEndingDirectorySeparator( home );
        var resolvedHome = CanonicalPathBoundary.TryCreate( home, out var boundary ) && boundary is not null ? boundary.CanonicalRoot : lexicalHome;

        return ExcludedFolderPaths.IsSameOrDescendant( canonical, lexicalHome ) || ExcludedFolderPaths.IsSameOrDescendant( canonical, resolvedHome );
    }

    // A user rule matching the path, wherever the indexed folder is.
    internal ExcludedFolderRule? MatchUser( string path ) =>
        Rules.FirstOrDefault( rule => rule.IsUser && rule.Contains( path ) );

    // A built-in rule matching the path whose folder lies strictly inside the
    // indexed folder (given in its lexical and canonical forms).
    internal ExcludedFolderRule? MatchBuiltIn( string path, string lexicalRoot, string canonicalRoot, bool directory ) =>
        Rules.FirstOrDefault( rule => !rule.IsUser && rule.ContainsBelowRoot( path, lexicalRoot, canonicalRoot, directory ) );

    internal ExcludedFolderRule? Match( string path, string lexicalRoot, string canonicalRoot, bool directory ) =>
        MatchUser( path ) ?? MatchBuiltIn( path, lexicalRoot, canonicalRoot, directory );

    // The rule text of a built-in name, app cache or transient file rule
    // matching the path below the indexed folder, or null.
    internal string? MatchPattern( string path, string lexicalRoot, string canonicalRoot, bool directory )
    {
        var name = NameRules.FirstOrDefault( rule => rule.Matches( path, lexicalRoot, canonicalRoot, directory ) );
        if ( name is not null )
        {
            return name.Display;
        }

        var scope = ScopeRules.FirstOrDefault( rule => rule.Matches( path, lexicalRoot, canonicalRoot, directory ) );
        if ( scope is not null )
        {
            return scope.Display;
        }

        return IsTransientFile( path, directory ) ? TransientFilesId : null;
    }

    private bool IsTransientFile( string path, bool directory ) => TransientFilesId is not null && !directory && TransientFiles.IsTransient( path );

    // Name, scope, file and tag rules have no folder range to purge by.
    internal static bool PatternsDiffer( ExcludedFolderRules before, ExcludedFolderRules after ) =>
        !string.Equals( before.PatternKey, after.PatternKey, StringComparison.Ordinal );

    // The folders whose coverage differs between two rule sets, in both forms.
    internal static IReadOnlyList<string> ChangedFolders( ExcludedFolderRules before, ExcludedFolderRules after )
    {
        var previous = before.Rules.ToHashSet( );
        var next = after.Rules.ToHashSet( );

        return previous
            .Except( next )
            .Concat( next.Except( previous ) )
            .SelectMany( static rule => rule.CanonicalPath is null ? [rule.LexicalPath] : new[] { rule.LexicalPath, rule.CanonicalPath } )
            .Distinct( StringComparer.Ordinal )
            .ToArray( );
    }

    private static ExcludedFolderRule CreateRule( string id, string source, string path )
    {
        return new ExcludedFolderRule( id, source, path, ResolveCanonical( path ) );
    }

    // The folder's real path, through its nearest existing ancestor when the
    // folder itself is missing. Null when that equals the lexical path.
    private static string? ResolveCanonical( string path )
    {
        try
        {
            var existing = path;
            var remainder = "";

            while ( !Directory.Exists( existing ) && Path.GetDirectoryName( existing ) is { } parent )
            {
                remainder = Path.Join( Path.GetFileName( existing ), remainder );
                existing = parent;
            }

            if ( !CanonicalPathBoundary.TryCreate( existing, out var boundary ) || boundary is null )
            {
                return null;
            }

            var canonical = Path.TrimEndingDirectorySeparator( Path.Join( boundary.CanonicalRoot, remainder ) );

            return string.Equals( canonical, path, StringComparison.Ordinal ) ? null : canonical;
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException )
        {
            return null;
        }
    }

    private static string ComputePatternKey( IReadOnlyList<ExcludedNameRule> names, IReadOnlyList<ExcludedScopeRule> scopes, string? transientFilesId,
        string? cacheDirectoryTagsId )
    {
        StringBuilder text = new( );

        foreach ( var rule in names.OrderBy( static rule => rule.Id, StringComparer.Ordinal ) )
        {
            text.Append( "name\0" ).Append( rule.Id ).Append( '\0' ).AppendJoin( '/', rule.Segments ).Append( '\n' );
        }

        var ordered = scopes.OrderBy( static rule => rule.Id, StringComparer.Ordinal ).ThenBy( static rule => rule.LexicalScope, StringComparer.Ordinal );

        foreach ( var rule in ordered )
        {
            text.Append( "scope\0" ).Append( rule.Id ).Append( '\0' ).Append( rule.LexicalScope ).Append( '\0' ).Append( rule.CanonicalScope ).Append( '\0' )
                .Append( rule.TopLevel ).Append( '\0' ).AppendJoin( '/', rule.Names.Order( StringComparer.Ordinal ) ).Append( '\n' );
        }

        return text.Append( "files\0" ).Append( transientFilesId ).Append( "\ntags\0" ).Append( cacheDirectoryTagsId ).ToString( );
    }

    private static string ComputeFingerprint( IReadOnlyList<ExcludedFolderRule> rules, string patternKey )
    {
        StringBuilder text = new( FingerprintVersion );
        text.Append( '\n' ).Append( patternKey );

        var ordered = rules
            .OrderBy( static rule => rule.Source, StringComparer.Ordinal )
            .ThenBy( static rule => rule.LexicalPath, StringComparer.Ordinal );

        foreach ( var rule in ordered )
        {
            text.Append( '\n' ).Append( rule.Source ).Append( '\0' ).Append( rule.LexicalPath ).Append( '\0' ).Append( rule.CanonicalPath ).Append( '\0' )
                .AppendJoin( '|', rule.Keep );
        }

        return Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( text.ToString( ) ) ) );
    }
}

// The rules the daemon applies now. The coordinator publishes a new snapshot
// after a preference commit; readers capture Current once per decision.
public sealed class ExcludedFolderRulesSource
{
    private ExcludedFolderRules _current = ExcludedFolderRules.Defaults( );

    public ExcludedFolderRules Current => Volatile.Read( ref _current );

    internal void Publish( ExcludedFolderRules rules ) => Volatile.Write( ref _current, rules );
}

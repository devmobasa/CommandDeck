using System.Globalization;

namespace CommandDeck;

internal readonly record struct PackageSizes( long? InstalledBytes, long? DownloadBytes );

/// <summary>
/// Turns pacman and expac stdout into records. Input is untrusted process
/// text: every line that does not match the expected shape is dropped.
/// </summary>
internal static class PacmanOutputParser
{
    private const string InstalledMarker = "[installed]";
    private const string InstalledVersionPrefix = "[installed: ";
    private const string MissingTargetMarker = "target not found:";
    private const string DependencyLinePrefix = "::";

    internal static List<PackageRecord> ParseSearchOutput( string output )
    {
        List<PackageRecord> records = [];
        SearchHeader? pending = null;

        foreach ( var line in SplitLines( output ) )
        {
            if ( line.Length == 0 )
            {
                continue;
            }

            if ( char.IsWhiteSpace( line[0] ) )
            {
                if ( NeedsSearchDescription( pending ) )
                {
                    pending.Description = line.Trim( );
                }

                continue;
            }

            AppendRecord( records, pending );
            pending = ParseSearchHeader( line );
        }

        AppendRecord( records, pending );

        return records;
    }

    private static bool NeedsSearchDescription( [System.Diagnostics.CodeAnalysis.NotNullWhen( true )] SearchHeader? pending )
    {
        return pending is { Description: null };
    }

    internal static List<PackageTransactionEntry> ParseInstallPlan( string output, IReadOnlyList<string> requested )
    {
        List<PackageTransactionEntry> entries = [];

        foreach ( var fields in ParsePlanLines( output ) )
        {
            entries.Add( CreateInstallPlanEntry( fields, requested ) );
        }

        return entries;
    }

    private static PackageTransactionEntry CreateInstallPlanEntry( string[] fields, IReadOnlyList<string> requested )
    {
        return new PackageTransactionEntry
        {
            Name = fields[0],
            Repository = FieldOrNull( fields, 2 ),
            CurrentVersion = null,
            TargetVersion = FieldOrNull( fields, 1 ),
            Requested = Contains( requested, fields[0] ),
            DownloadBytes = fields.Length > 3 ? TryParseSize( fields[3] ) : null
        };
    }

    internal static List<PackageTransactionEntry> ParseRemovePlan( string output, IReadOnlyList<string> requested )
    {
        List<PackageTransactionEntry> entries = [];

        foreach ( var fields in ParsePlanLines( output ) )
        {
            entries.Add( CreateRemovePlanEntry( fields, requested ) );
        }

        return entries;
    }

    private static PackageTransactionEntry CreateRemovePlanEntry( string[] fields, IReadOnlyList<string> requested )
    {
        return new PackageTransactionEntry
        {
            Name = fields[0],
            Repository = null,
            CurrentVersion = FieldOrNull( fields, 1 ),
            TargetVersion = null,
            Requested = Contains( requested, fields[0] )
        };
    }

    internal static Dictionary<string, PackageSizes> ParseExpacOutput( string output )
    {
        Dictionary<string, PackageSizes> sizes = new( StringComparer.Ordinal );

        foreach ( var line in SplitLines( output ) )
        {
            var fields = line.Split( '\t' );
            if ( fields.Length < 2 )
            {
                continue;
            }

            var name = fields[0]
                .Trim( );

            if ( !AgentContractValidator.IsValidPackageName( name ) )
            {
                continue;
            }

            sizes[name] = new PackageSizes( TryParseSize( fields[1] ), fields.Length > 2 ? TryParseSize( fields[2] ) : null );
        }

        return sizes;
    }

    internal static Dictionary<string, string> ParseInstalledVersions( string output )
    {
        Dictionary<string, string> versions = new( StringComparer.Ordinal );

        foreach ( var line in SplitLines( output ) )
        {
            var trimmed = line.Trim( );
            var separator = trimmed.IndexOf( ' ' );
            if ( separator <= 0 )
            {
                continue;
            }

            var name = trimmed[..separator];
            if ( AgentContractValidator.IsValidPackageName( name ) )
            {
                versions[name] = trimmed[(separator + 1)..]
                    .Trim( );
            }
        }

        return versions;
    }

    internal static List<string> CollectMissingTargets( string standardError )
    {
        List<string> missing = [];

        foreach ( var line in SplitLines( standardError ) )
        {
            var marker = line.IndexOf( MissingTargetMarker, StringComparison.Ordinal );
            if ( marker < 0 )
            {
                continue;
            }

            var name = line[(marker + MissingTargetMarker.Length)..]
                .Trim( );

            if ( IsNewMissingTarget( name, missing ) )
            {
                missing.Add( name );
            }
        }

        return missing;
    }

    private static bool IsNewMissingTarget( string name, List<string> missing )
    {
        return AgentContractValidator.IsValidPackageName( name ) && !missing.Contains( name, StringComparer.Ordinal );
    }

    internal static List<string> CollectDependencyLines( string standardError )
    {
        List<string> lines = [];

        foreach ( var line in SplitLines( standardError ) )
        {
            var trimmed = line.Trim( );
            if ( IsDependencyLine( trimmed ) )
            {
                lines.Add( trimmed );
            }
        }

        return lines;
    }

    private static bool IsDependencyLine( string trimmed )
    {
        return trimmed.StartsWith( DependencyLinePrefix, StringComparison.Ordinal ) && trimmed.Length > DependencyLinePrefix.Length;
    }

    internal static long? TryParseSize( string value )
    {
        var trimmed = value.Trim( );
        if ( trimmed.Length == 0 )
        {
            return null;
        }

        if ( long.TryParse( trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact ) )
        {
            return exact >= 0 ? exact : null;
        }

        if ( double.TryParse( trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var approximate ) && approximate is >= 0 and < long.MaxValue )
        {
            return ( long ) Math.Round( approximate );
        }

        return null;
    }

    private static List<string[]> ParsePlanLines( string output )
    {
        List<string[]> parsed = [];

        foreach ( var line in SplitLines( output ) )
        {
            var trimmed = line.Trim( );
            if ( IsEmptyPlanLine( trimmed ) )
            {
                continue;
            }

            var fields = trimmed.Split( '|' );
            if ( HasValidPlanName( fields ) )
            {
                fields[0] = fields[0]
                    .Trim( );

                parsed.Add( fields );
            }
        }

        return parsed;
    }

    private static bool IsEmptyPlanLine( string trimmed )
    {
        return trimmed.Length == 0 || !trimmed.Contains( '|', StringComparison.Ordinal );
    }

    private static bool HasValidPlanName( string[] fields )
    {
        return fields.Length >= 2 &&
               AgentContractValidator.IsValidPackageName( fields[0]
                   .Trim( ) );
    }

    private static SearchHeader? ParseSearchHeader( string line )
    {
        var slash = line.IndexOf( '/' );
        var firstSpace = line.IndexOf( ' ' );
        if ( HasInvalidSearchHeader( slash, firstSpace ) )
        {
            return null;
        }

        var name = line[(slash + 1)..firstSpace];
        if ( !AgentContractValidator.IsValidPackageName( name ) )
        {
            return null;
        }

        var remainder = line[(firstSpace + 1)..]
            .Trim( );

        var versionEnd = remainder.IndexOf( ' ' );
        var version = versionEnd < 0 ? remainder : remainder[..versionEnd];
        var tail = versionEnd < 0 ? string.Empty : remainder[(versionEnd + 1)..];

        return CreateSearchHeader( name, line, slash, version, tail );
    }

    private static bool HasInvalidSearchHeader( int slash, int firstSpace )
    {
        return slash <= 0 || firstSpace <= slash + 1;
    }

    private static SearchHeader CreateSearchHeader( string name, string line, int slash, string version, string tail )
    {
        return new SearchHeader
        {
            Name = name,
            Repository = line[..slash],
            Version = version.Length == 0 ? null : version,
            Installed = tail.Contains( InstalledMarker, StringComparison.Ordinal ) || tail.Contains( InstalledVersionPrefix, StringComparison.Ordinal ),
            InstalledVersion = ReadInstalledVersion( tail, version )
        };
    }

    private static string? ReadInstalledVersion( string tail, string repositoryVersion )
    {
        var marker = tail.IndexOf( InstalledVersionPrefix, StringComparison.Ordinal );
        if ( marker >= 0 )
        {
            var end = tail.IndexOf( ']', marker );
            if ( end > marker )
            {
                var version = tail[(marker + InstalledVersionPrefix.Length)..end]
                    .Trim( );

                return version.Length == 0 ? null : version;
            }
        }

        if ( tail.Contains( InstalledMarker, StringComparison.Ordinal ) && repositoryVersion.Length > 0 )
        {
            return repositoryVersion;
        }

        return null;
    }

    private static void AppendRecord( List<PackageRecord> records, SearchHeader? header )
    {
        if ( header is null )
        {
            return;
        }

        records.Add( new PackageRecord
        {
            Name = header.Name,
            Repository = header.Repository,
            Version = header.Version,
            Description = header.Description,
            Installed = header.Installed,
            InstalledVersion = header.InstalledVersion
        } );
    }

    private static string? FieldOrNull( string[] fields, int index )
    {
        if ( index >= fields.Length )
        {
            return null;
        }

        var value = fields[index]
            .Trim( );

        return value.Length == 0 ? null : value;
    }

    private static bool Contains( IReadOnlyList<string> names, string name )
    {
        foreach ( var candidate in names )
        {
            if ( string.Equals( candidate, name, StringComparison.Ordinal ) )
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitLines( string output )
    {
        if ( string.IsNullOrEmpty( output ) )
        {
            yield break;
        }

        foreach ( var line in output.Split( '\n' ) )
        {
            yield return line.TrimEnd( '\r' );
        }
    }

    private sealed class SearchHeader
    {
        public required string Name { get; init; }
        public string? Repository { get; init; }
        public string? Version { get; init; }
        public bool Installed { get; init; }
        public string? InstalledVersion { get; init; }
        public string? Description { get; set; }
    }
}

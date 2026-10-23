namespace CommandDeck.PackageHelper;

/// <summary>
/// Digest of the exact transaction Pacman printed for a request. The daemon
/// binds its preview token to this value and the helper recomputes it before
/// it commits anything.
/// </summary>
/// <remarks>
/// The digest is the lowercase SHA-256 hex digest of <c>operation\n</c>
/// followed by one <c>name|version|repository\n</c> line per printed entry,
/// sorted ordinal. Removal entries carry an empty repository field. Lines
/// that do not name a valid package are dropped, exactly as the daemon's
/// <c>PacmanOutputParser</c> drops them, so both sides digest the same set.
/// </remarks>
internal static class PacmanTransactionDigest
{
    public static IReadOnlyList<string> BuildPrintArguments( string operation, IReadOnlyList<string> names )
    {
        ArgumentNullException.ThrowIfNull( names );

        return operation switch
        {
            PackageOperations.Install => PacmanArguments.BuildInstallPrintArguments( names, PacmanArguments.InstallDigestPrintFormat ),
            PackageOperations.Remove => PacmanArguments.BuildRemovePrintArguments( names ),
            _ => throw new ArgumentOutOfRangeException( nameof( operation ) )
        };
    }

    public static string Compute( string operation, string printedOutput )
    {
        ArgumentException.ThrowIfNullOrEmpty( operation );

        List<string> entries = [];

        foreach ( var rawLine in (printedOutput ?? string.Empty).Split( '\n' ) )
        {
            var line = rawLine
                .TrimEnd( '\r' )
                .Trim( );

            if ( !line.Contains( '|', StringComparison.Ordinal ) )
            {
                continue;
            }

            var fields = line.Split( '|' );
            if ( !HasValidPackageFields( fields ) )
            {
                continue;
            }

            var name = fields[0]
                .Trim( );

            entries.Add( PackageTransactionDigest.SerializeRow( name, Field( fields, 1 ), Field( fields, 2 ) ) );
        }

        return PackageTransactionDigest.Compute( operation, entries );
    }

    public static IReadOnlyDictionary<string, string> ParseVersions( string printedOutput )
    {
        Dictionary<string, string> versions = new( StringComparer.Ordinal );

        foreach ( var rawLine in (printedOutput ?? string.Empty).Split( '\n' ) )
        {
            var fields = rawLine
                .TrimEnd( '\r' )
                .Trim( )
                .Split( '|' );

            if ( HasValidPackageFields( fields ) )
            {
                versions[fields[0]
                    .Trim( )] = fields[1]
                    .Trim( );
            }
        }

        return versions;
    }

    private static string Field( string[] fields, int index )
    {
        if ( index >= fields.Length )
        {
            return string.Empty;
        }

        return fields[index]
            .Trim( );
    }

    private static bool HasValidPackageFields( string[] fields )
    {
        return fields.Length >= 2 &&
               AgentContractValidator.IsValidPackageName( fields[0]
                   .Trim( ) );
    }
}

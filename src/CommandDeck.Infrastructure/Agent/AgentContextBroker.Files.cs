using System.Text;

namespace CommandDeck;

public sealed partial class AgentContextBroker
{
    public IReadOnlyList<AgentDirectoryEntry> ListDirectory( string canonicalDirectory, int maximumEntries )
    {
        using var opened = OpenAuthorizedDirectory( canonicalDirectory );
        List<AgentDirectoryEntry> entries = [];
        EnumerationOptions options = CreateListingOptions( );
        foreach ( var path in Directory
                     .EnumerateFileSystemEntries( opened.DescriptorPath, "*", options )
                     .Order( StringComparer.Ordinal ) )
        {
            if ( entries.Count >= maximumEntries )
            {
                break;
            }

            var name = Path.GetFileName( path );
            if ( IsProtected( Path.Combine( opened.CanonicalPath, name ) ) )
            {
                continue;
            }

            var attributes = File.GetAttributes( path );
            var link = (attributes & FileAttributes.ReparsePoint) != 0;
            var directory = (attributes & FileAttributes.Directory) != 0;
            var size = directory || link ? 0 : new FileInfo( path ).Length;
            entries.Add( new AgentDirectoryEntry( name, GetEntryKind( link, directory ), size ) );
        }

        return entries;
    }

    public string ReadTextFile( string canonicalFile, int maximumBytes, out bool truncated )
    {
        using var file = OpenAuthorizedFile( canonicalFile );

        return ReadOpenedTextFile( file, maximumBytes, out truncated );
    }

    private static string ReadOpenedTextFile( SafeRegularFile file, int maximumBytes, out bool truncated )
    {
        var length = ( int ) Math.Min( file.Length, maximumBytes );
        truncated = file.Length > maximumBytes;
        var buffer = new byte[length];
        var total = 0;
        while ( total < length )
        {
            var read = RandomAccess.Read( file.Handle, buffer.AsSpan( total ), total );
            if ( read <= 0 )
            {
                break;
            }

            total += read;
        }

        if ( buffer
                 .AsSpan( 0, Math.Min( total, FileReadBufferBytes ) )
                 .IndexOf( ( byte ) 0 ) >=
             0 )
        {
            throw new InvalidOperationException( "The file is binary and cannot be read as text." );
        }

        try
        {
            return new UTF8Encoding( false, true ).GetString( buffer, 0, total );
        }
        catch ( DecoderFallbackException )
        {
            throw new InvalidOperationException( "The file is not valid UTF-8 text." );
        }
    }

    public IReadOnlyList<AgentSearchMatch> SearchText( string canonicalDirectory, string query, int maximumMatches, int maximumBytes,
        CancellationToken cancellationToken )
    {
        if ( IsInvalidSearchQuery( query ) )
        {
            throw new InvalidOperationException( "The search query is empty or too long." );
        }

        List<AgentSearchMatch> matches = [];
        long budget = maximumBytes;
        EnumerationOptions options = CreateSearchOptions( );
        using var opened = OpenAuthorizedDirectory( canonicalDirectory );
        foreach ( var path in Directory.EnumerateFiles( opened.DescriptorPath, "*", options ) )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            if ( HasReachedSearchBudget( maximumMatches, matches, budget ) )
            {
                break;
            }

            var relative = Path.GetRelativePath( opened.DescriptorPath, path );
            var expectedPath = Path.GetFullPath( Path.Combine( opened.CanonicalPath, relative ) );
            if ( IsProtected( expectedPath ) )
            {
                continue;
            }

            SearchOneFile( path, expectedPath, query, matches, maximumMatches, ref budget );
        }

        return matches;
    }

    private void SearchOneFile( string path, string expectedPath, string query, List<AgentSearchMatch> matches, int maximumMatches, ref long budget )
    {
        string text;

        try
        {
            using var file = SafeRegularFile.Open( path, followLeafSymbolicLink: false );
            EnsureOpenedPath( expectedPath, file.CanonicalPath );
            text = ReadOpenedTextFile( file, ( int ) Math.Min( AgentLimits.MaximumFileBytes, budget ), out _ );
        }
        catch ( InvalidOperationException )
        {
            return;
        }

        budget -= Encoding.UTF8.GetByteCount( text );
        var line = 0;

        foreach ( var candidate in text.Split( '\n' ) )
        {
            line++;

            if ( candidate.Contains( query, StringComparison.OrdinalIgnoreCase ) )
            {
                var trimmed = candidate.Length > MaximumDisplayNameCharacters ? candidate[..MaximumDisplayNameCharacters] : candidate;

                matches.Add( new AgentSearchMatch( Path.GetRelativePath( _home, expectedPath ), line, trimmed.TrimEnd( '\r' ) ) );

                if ( matches.Count >= maximumMatches )
                {
                    return;
                }
            }
        }
    }

    private static EnumerationOptions CreateListingOptions( )
    {
        return new( ) { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true };
    }

    private static string GetEntryKind( bool link, bool directory )
    {
        if ( link )
        {
            return LinkEntryKind;
        }

        if ( directory )
        {
            return DirectoryEntryKind;
        }

        return FileEntryKind;
    }

    private static bool IsInvalidSearchQuery( string query )
    {
        return string.IsNullOrWhiteSpace( query ) || query.Length > MaximumSearchQueryCharacters;
    }

    private static EnumerationOptions CreateSearchOptions( )
    {
        return new( )
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaximumSearchDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
    }

    private static bool HasReachedSearchBudget( int maximumMatches, List<AgentSearchMatch> matches, long budget )
    {
        return matches.Count >= maximumMatches || budget <= 0;
    }
}

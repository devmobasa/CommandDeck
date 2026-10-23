namespace CommandDeck;

internal sealed partial class SqliteLauncherPreferenceStore
{
    private void ValidatePreferences( LauncherPreferences preferences )
    {
        ArgumentNullException.ThrowIfNull( preferences );

        var expected = ExpectedSourceIds( );
        var actual = ActualSourceIds( preferences );

        var known = ContributionSnapshot
            .Sources.Select( source => source.Id )
            .ToHashSet( StringComparer.Ordinal );

        if ( HasInvalidSourceIds( expected, actual, known ) )
        {
            throw new InvalidOperationException( "Source preferences must contain each known source exactly once." );
        }

        if ( HasInvalidSourcePositions( preferences, actual ) )
        {
            throw new InvalidOperationException( "Source preference positions must be a complete unique permutation." );
        }

        if ( preferences.PreferredEditor is not EditorKinds.Graphical and not EditorKinds.Terminal )
        {
            throw new InvalidOperationException( "Preferred editor must be graphical or terminal." );
        }

        if ( !HeyOpenTargets.IsKnown( preferences.HeyOpenTarget ) )
        {
            throw new InvalidOperationException( "HEY threads open in the web app or the terminal app." );
        }

        _ = EditorAdapterValidator.Validate( preferences.GraphicalEditor, terminal: false );
        _ = EditorAdapterValidator.Validate( preferences.TerminalEditor, terminal: true );
        EnsureHomeAvailable( preferences.Sources );
    }

    private string[] ExpectedSourceIds( )
    {
        return ContributionSnapshot
            .Sources.Select( static value => value.Id )
            .Order( StringComparer.Ordinal )
            .ToArray( );
    }

    private static string[] ActualSourceIds( LauncherPreferences preferences )
    {
        return preferences
            .Sources.Select( static value => value.SourceId )
            .Order( StringComparer.Ordinal )
            .ToArray( );
    }

    private static bool HasInvalidSourceIds( string[] expected, string[] actual, HashSet<string> known )
    {
        return expected
                   .Except( actual, StringComparer.Ordinal )
                   .Any( ) ||
               actual
                   .Distinct( StringComparer.Ordinal )
                   .Count( ) !=
               actual.Length ||
               actual.Any( id => !known.Contains( id ) );
    }

    private static bool HasInvalidSourcePositions( LauncherPreferences preferences, string[] actual )
    {
        return preferences
                   .Sources.Select( static value => value.Position )
                   .Distinct( )
                   .Count( ) !=
               actual.Length ||
               preferences.Sources.Any( value => value.Position < 1 || value.Position > actual.Length );
    }

    // A source whose rows never appear on Home (HEY mail) cannot be switched on there.
    private void EnsureHomeAvailable( IEnumerable<SourcePreference> sources )
    {
        var unavailable = UnavailableHomeSources( );

        var blocked = sources.FirstOrDefault( source => source.EnabledOnHome && unavailable.Contains( source.SourceId ) );
        if ( blocked is not null )
        {
            throw new InvalidOperationException( $"{ContributionSnapshot.Label( blocked.SourceId )} never appears on Home." );
        }
    }

    private HashSet<string> UnavailableHomeSources( )
    {
        return ContributionSnapshot
            .Sources.Where( static source => !source.HomeAvailable )
            .Select( static source => source.Id )
            .ToHashSet( StringComparer.Ordinal );
    }
}

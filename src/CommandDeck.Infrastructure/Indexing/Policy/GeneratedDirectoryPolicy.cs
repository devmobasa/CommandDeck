namespace CommandDeck;

internal sealed class GeneratedDirectoryPolicy( IndexPolicyInputs inputs )
{
    public string? Match( string directory ) => Match( directory, markers: null, parentMarkers: null );

    // Traversal passes the marker sets it already holds for the directory and
    // its parent; missing sets are read (and observed) on demand.
    public string? Match( string directory, IReadOnlySet<string>? markers, IReadOnlySet<string>? parentMarkers )
    {
        var name = Path.GetFileName( directory );
        if ( IsAlwaysGenerated( name ) )
        {
            return name;
        }

        if ( (markers ?? inputs.Markers( directory )).Contains( IndexingConstants.PythonEnvironmentMarker ) )
        {
            return "Python virtual environment (pyvenv.cfg)";
        }

        if ( parentMarkers is null )
        {
            var parent = Path.GetDirectoryName( directory );
            if ( parent is null )
            {
                return null;
            }

            parentMarkers = inputs.Markers( parent );
        }

        return MatchProjectOutput( name, parentMarkers );
    }

    public static bool IsAlwaysGenerated( string name ) => name is "node_modules" or "__pycache__" or IndexingConstants.GitDirectoryName;

    private static string? MatchProjectOutput( string name, IReadOnlySet<string> markers ) =>
        name switch
        {
            "dist" or ".next" or ".nuxt" or ".svelte-kit" or ".output" when markers.Contains( ProjectMarkerNames.PackageJson ) => $"JavaScript project {name}",
            "target" when markers.Contains( ProjectMarkerNames.CargoToml ) => "Cargo target",
            "target" when markers.Contains( ProjectMarkerNames.PomXml ) => "Maven target",
            "bin" or "obj" when markers.Any( IndexPolicyInputs.IsDotNetProject ) => $".NET project {name}",
            "build" or ".gradle" when IsGradleProject( markers ) => $"Gradle project {name}",
            ".venv" or "venv" when IsPythonProject( markers ) => $"Python project {name}",
            "vendor" when markers.Contains( ProjectMarkerNames.ComposerJson ) => "Composer vendor",
            _ => null
        };

    private static bool IsGradleProject( IReadOnlySet<string> markers ) => markers.Any( ProjectMarkerNames.IsGradleMarker );

    private static bool IsPythonProject( IReadOnlySet<string> markers ) => markers.Any( ProjectMarkerNames.IsPythonMarker );
}

namespace CommandDeck.Specs;

public static partial class Program
{
    // Simulates the service manager's literal-dollar handling. These editors
    // are short capture fixtures; the detached lifetime has its own regression.
    private sealed class EditorLaunchFixture( string? previousPath ) : IDisposable
    {
        public void Dispose( ) => Environment.SetEnvironmentVariable( "PATH", previousPath );
    }

    private static async Task<EditorLaunchFixture> CreateEditorLaunchFixtureAsync( string root )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This editor fixture requires Linux." );
        }

        await WriteExecutableAsync( Path.Combine( root, "uwsm-app" ), """
            #!/bin/bash
            while [[ $# -gt 0 && $1 != -- ]]; do shift; done
            shift
            executable=$1
            shift
            arguments=()
            for argument in "$@"; do
              arguments+=("${argument//\$\$/\$}")
            done
            exec "$executable" "${arguments[@]}"
            """ );
        var previousPath = Environment.GetEnvironmentVariable( "PATH" );
        Environment.SetEnvironmentVariable( "PATH", root + Path.PathSeparator + previousPath );

        return new( previousPath );
    }
}

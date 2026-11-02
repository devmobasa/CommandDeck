using CommandDeck.PackageHelper;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditPackagePipeDeadlineAsync( )
    {
        foreach ( var redirection in new[] { "2>/dev/null", ">/dev/null" } )
        {
            // The parent exits immediately. Only its short-lived descendant
            // retains one of the redirected pipes; it cleans itself up.
            var run = BoundedProcess.RunAsync( "/bin/sh", ["-c", $"/usr/bin/sleep 2 {redirection} & exit 0"],
                1024, TimeSpan.FromMilliseconds( 100 ), default );
            try
            {
                var result = await run.WaitAsync( TimeSpan.FromSeconds( 1 ) );
                True( result.TimedOut );
                Equal( -1, result.ExitCode );
            }
            finally
            {
                await run;
            }
        }
    }
}

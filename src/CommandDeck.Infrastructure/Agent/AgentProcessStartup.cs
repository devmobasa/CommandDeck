using System.Diagnostics;

namespace CommandDeck;

internal static class AgentProcessStartup
{
    // Before a handle is handed to the caller, the launcher owns termination,
    // reaping and disposal, including cancellation immediately after Start.
    public static async ValueTask DisposeAsync( Process process )
    {
        using ( process )
        {
            try
            {
                if ( !process.HasExited )
                {
                    process.Kill( entireProcessTree: true );
                }

                await process.WaitForExitAsync( )
                    .ConfigureAwait( false );
            }
            catch ( InvalidOperationException )
            {
                // Process.Start failed before assigning a process handle.
            }
        }
    }
}

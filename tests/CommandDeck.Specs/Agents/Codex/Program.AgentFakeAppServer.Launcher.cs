namespace CommandDeck.Specs;

public static partial class Program
{
    private sealed class FakeCodexLauncher : IAgentProcessLauncher
    {
        public List<CodexLaunchRequest> Requests { get; } = [];

        public bool HoldAccountReadReply { get; set; }

        public FakeAppServer? Current { get; private set; }

        public Func<FakeAppServer, string, string, Task>? TurnScript { get; }

        public bool Authenticated { get; set; }

        // Holds the next launch after the backend admitted it, as a slow spawn would.
        public TaskCompletionSource<bool>? HoldNextLaunch { get; set; }

        public TaskCompletionSource LaunchEntered { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

        public List<FakeAppServer> Servers { get; } = [];

        public async ValueTask<IAgentProcessHandle> LaunchAsync( CodexLaunchRequest request, CancellationToken cancellationToken )
        {
            Requests.Add( request );
            var hold = HoldNextLaunch;
            if ( hold is not null )
            {
                HoldNextLaunch = null;
                LaunchEntered.TrySetResult( );
                await hold.Task.WaitAsync( cancellationToken );
            }

            Current = new FakeAppServer
            {
                RuntimeHome = request.RuntimeHome,
                HoldAccountReadReply = HoldAccountReadReply,
                TurnScript = TurnScript,
                Authenticated = Authenticated || File.Exists( Path.Combine( request.RuntimeHome, "auth.json" ) )
            };

            Servers.Add( Current );

            return Current;
        }
    }

    private static string CreateFakeCodexBinary( string root )
    {
        var path = Path.Combine( root, "codex" );
        File.WriteAllText( path, $"#!/bin/sh\necho 'codex-cli {CodexAppServerContract.ReviewedVersion}'\n" );

        if ( OperatingSystem.IsLinux( ) )
        {
            File.SetUnixFileMode( path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
        }

        return path;
    }

    private static AgentOptions CreateAgentOptions( string root, string binaryPath ) => new( )
    {
        CodexBinaryPath = binaryPath,
        RuntimeDirectory = Path.Combine( root, "runtime" ),
        StateDirectory = Path.Combine( root, "state" ),
        BubblewrapPath = "/usr/bin/bwrap",
        SystemdRunPath = "/usr/bin/systemd-run"
    };
}

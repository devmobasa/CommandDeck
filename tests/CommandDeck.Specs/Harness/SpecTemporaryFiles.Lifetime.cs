using System.Runtime.InteropServices;

namespace CommandDeck.Specs;

public static partial class Program
{
    private sealed partial class SpecTemporaryFiles
    {
        internal static IDisposable? HandleTermination( string[] args )
        {
            // This child deliberately owns SIGTERM to test graceful extension stop.
            if ( !OperatingSystem.IsLinux( ) || args.Contains( "--extension-disposal-fixture", StringComparer.Ordinal ) )
            {
                return null;
            }

            return new TerminationSignals( );
        }

        private sealed class TerminationSignals : IDisposable
        {
            private const int InterruptExitCode = 130;
            private const int TerminateExitCode = 143;
            private readonly PosixSignalRegistration _interrupt = PosixSignalRegistration.Create( PosixSignal.SIGINT, Exit );
            private readonly PosixSignalRegistration _terminate = PosixSignalRegistration.Create( PosixSignal.SIGTERM, Exit );

            private static void Exit( PosixSignalContext context )
            {
                context.Cancel = true;

                // ProcessExit runs the same idempotent cleanup as normal disposal.
                Environment.Exit( context.Signal == PosixSignal.SIGINT ? InterruptExitCode : TerminateExitCode );
            }

            public void Dispose( )
            {
                _interrupt.Dispose( );
                _terminate.Dispose( );
            }
        }

        private static void DeleteOwnedTree( string directory, bool recovering = false )
        {
            List<FileStream> leases = [];
            List<string> runs = [directory];

            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach ( var child in Directory.EnumerateDirectories( directory, RunPrefix + "*", options ) )
                {
                    if ( IsOwnedRun( child ) )
                    {
                        if ( recovering )
                        {
                            leases.Add( new FileStream( Path.Combine( child, LeaseFileName ), FileMode.Open, FileAccess.ReadWrite, FileShare.None ) );
                        }

                        runs.Add( child );
                    }
                }

                // An exiting owner removes its entire synthetic tree. Recovery
                // first acquires every nested lease to preserve surviving children.
                foreach ( var run in runs )
                {
                    var runName = Path.GetFileName( run );
                    var sockets = new DirectoryInfo( Path.Combine( "/tmp", SocketDirectoryPrefix + runName[RunPrefix.Length..] ) );
                    if ( sockets is { Exists: true, LinkTarget: null } )
                    {
                        sockets.Delete( recursive: true );
                    }
                }

                Directory.Delete( directory, recursive: true );
            }
            finally
            {
                foreach ( var lease in leases )
                {
                    lease.Dispose( );
                }
            }
        }

        private static void RecoverStaleRuns( string parent )
        {
            foreach ( var candidate in new DirectoryInfo( parent ).EnumerateDirectories( RunPrefix + "*" ) )
            {
                var id = candidate.Name[RunPrefix.Length..];
                if ( IsIneligibleStaleRun( candidate, id ) )
                {
                    continue;
                }

                // Only reclaim our marked, old, unlocked directories. An active
                // run keeps this lease open even if its timestamps are old.
                var marker = Path.Combine( candidate.FullName, LeaseFileName );
                if ( !File.Exists( marker ) || new FileInfo( marker ).LinkTarget is not null )
                {
                    continue;
                }

                try
                {
                    using var lease = new FileStream( marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None );
                    DeleteOwnedTree( candidate.FullName, recovering: true );
                }
                catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
                {
                    // Locked runs and inaccessible directories belong to someone
                    // else; leave them for their owner or a later recovery.
                }
            }
        }

        private static bool IsOwnedRun( string child )
        {
            var runName = Path.GetFileName( child );

            return Guid.TryParseExact( runName[RunPrefix.Length..], "N", out _ ) && File.Exists( Path.Combine( child, LeaseFileName ) );
        }

        private static bool IsIneligibleStaleRun( DirectoryInfo candidate, string id )
        {
            return !Guid.TryParseExact( id, "N", out _ ) ||
                   candidate.LinkTarget is not null ||
                   candidate.LastWriteTimeUtc > DateTime.UtcNow.AddDays( -StaleRunAgeDays );
        }
    }
}

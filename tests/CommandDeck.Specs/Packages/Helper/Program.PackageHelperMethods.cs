using CommandDeck.PackageHelper;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task HelperRejectsUnsupportedOperationsAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );

        var failure = await CaptureAsync( ( ) => fixture.CommitAsync( operation: "upgrade", names: ["alpha"] ) );

        Equal( "org.commanddeck.Package1.Error.InvalidOperation", failure.ErrorName );
        Equal( 0, fixture.Polkit.Calls );
        True( !fixture.Pacman.RanCommit );
    }

    private static async Task HelperRejectsInvalidPackageNamesAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );

        var flagged = await CaptureAsync( ( ) => fixture.CommitAsync( PackageOperations.Install, ["--root"] ) );

        Equal( "org.commanddeck.Package1.Error.InvalidNames", flagged.ErrorName );

        string[] tooMany =
        [
            .. Enumerable
                .Range( 0, AgentLimits.MaximumPackageNames + 1 )
                .Select( static index => $"package{index}" )
        ];

        var excessive = await CaptureAsync( ( ) => fixture.CommitAsync( PackageOperations.Install, tooMany ) );

        Equal( "org.commanddeck.Package1.Error.InvalidNames", excessive.ErrorName );
        Equal( 0, fixture.Polkit.Calls );
        True( !fixture.Pacman.RanCommit );
    }

    private static async Task HelperRejectsAChangedDatabaseAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );

        var failure = await CaptureAsync( ( ) => fixture.CommitAsync( PackageOperations.Install, ["alpha"], databaseIdentity: new string( '0', 64 ) ) );

        Equal( "org.commanddeck.Package1.Error.DatabaseChanged", failure.ErrorName );
        Equal( 0, fixture.Polkit.Calls );
        True( !fixture.Pacman.RanCommit );
    }

    private static async Task HelperRejectsAChangedTransactionAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );

        var failure = await CaptureAsync( ( ) => fixture.CommitAsync( PackageOperations.Install, ["alpha"], transactionDigest: new string( '1', 64 ) ) );

        Equal( "org.commanddeck.Package1.Error.TransactionChanged", failure.ErrorName );
        Equal( 0, fixture.Polkit.Calls );
        True( !fixture.Pacman.RanCommit );
    }

    private static async Task HelperRevalidatesAfterPolkitAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.PrintOutput = "alpha|1.0|core\n";

        fixture.Polkit.BeforeReturnAsync = ( ) =>
        {
            File.WriteAllText( Path.Combine( fixture.DatabaseRoot, "sync", "core.db" ), "changed" );
            fixture.Pacman.PrintOutput = "alpha|2.0|core\n";

            return Task.CompletedTask;
        };

        var failure = await CaptureAsync( ( ) => fixture.CommitAsync( PackageOperations.Install, ["alpha"],
            transactionDigest: PacmanTransactionDigest.Compute( PackageOperations.Install, "alpha|1.0|core\n" ) ) );

        Equal( "org.commanddeck.Package1.Error.DatabaseChanged", failure.ErrorName );
        True( !fixture.Pacman.RanCommit );
    }

    private static async Task HelperRevalidatesQueuedTransactionAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.PrintOutput = "alpha|1.0|core\n";
        var operationId = await fixture.CommitAsync( PackageOperations.Install, ["alpha"] );
        fixture.Pacman.PrintOutput = "alpha|2.0|core\n";
        await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
        Equal( "failed", fixture.Core.GetOperation( operationId ).State );
        True( !fixture.Pacman.RanCommit );
    }

    private static async Task HelperRefusesToCommitWhileTheDatabaseIsLockedAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        File.WriteAllText( Path.Combine( fixture.DatabaseRoot, "db.lck" ), string.Empty );

        var failure = await CaptureAsync( ( ) => fixture.CommitAsync( PackageOperations.Install, ["alpha"] ) );

        Equal( "org.commanddeck.Package1.Error.DatabaseLocked", failure.ErrorName );
        Equal( 0, fixture.Polkit.Calls );
        True( !fixture.Pacman.RanCommit );
        var status = await fixture.Core.GetStatusAsync( CancellationToken.None );
        True( status.DatabaseLocked );
        Equal( "1", status.ProtocolVersion );
        Equal( "7.1.0", status.PacmanVersion );
    }

    private static async Task HelperDeniesUnauthorizedPackageChangesAsync( )
    {
        using var denied = PackageHelperFixture.Create( );
        denied.Polkit.Authorized = false;

        var refusal = await CaptureAsync( ( ) => denied.CommitAsync( PackageOperations.Install, ["alpha"] ) );

        Equal( "org.commanddeck.Package1.Error.NotAuthorized", refusal.ErrorName );
        Equal( 1, denied.Polkit.Calls );
        True( !denied.Pacman.RanCommit );
        Equal( "org.commanddeck.package.change", denied.Polkit.LastAction );
        Equal( "commanddeck-daemon", denied.Polkit.LastSender );
        Equal( "CommandDeck wants to change installed packages", denied.Polkit.Detail( "polkit.message" ) );
        Equal( "install", denied.Polkit.Detail( "package.operation" ) );
        Equal( "alpha", denied.Polkit.Detail( "package.names" ) );
        using var unattended = PackageHelperFixture.Create( );
        unattended.Polkit.Authorized = false;
        unattended.Polkit.Challenge = true;

        var missingAgent = await CaptureAsync( ( ) => unattended.CommitAsync( PackageOperations.Install, ["alpha"] ) );

        Equal( "org.commanddeck.Package1.Error.NoAuthenticationAgent", missingAgent.ErrorName );
        True( !unattended.Pacman.RanCommit );
    }

    private static async Task HelperRunsOnePackageOperationAtATimeAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        var first = await fixture.CommitAsync( PackageOperations.Install, ["alpha"] );
        True( first.Length > 0 );
        var busy = await CaptureAsync( ( ) => fixture.CommitAsync( PackageOperations.Install, ["alpha"] ) );
        Equal( "org.commanddeck.Package1.Error.Busy", busy.ErrorName );
        await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
        var second = await fixture.CommitAsync( PackageOperations.Install, ["alpha"] );
        True( !string.Equals( first, second, StringComparison.Ordinal ) );
    }

    private static async Task HelperInstallsThroughLiteralArgvAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.PrintOutput = "beta|2.0|extra\nalpha|1.0|core\n";
        fixture.Pacman.QueryOutput = "alpha 1.0\nbeta 2.0\n";
        var operationId = await fixture.CommitAsync( PackageOperations.Install, ["beta", "alpha"] );
        Equal( "queued", fixture.Core.GetOperation( operationId ).State );
        await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );

        Equal( "/usr/bin/pacman -S --print --print-format %n|%v|%r --needed -- alpha beta", fixture.Pacman.Invocations[0] );
        Equal( "/usr/bin/pacman -S --needed --noconfirm --noprogressbar --color never -- alpha beta", fixture.Pacman.Invocations[3] );
        Equal( "/usr/bin/pacman -Q -- alpha beta", fixture.Pacman.Invocations[4] );
        var state = fixture.Core.GetOperation( operationId );
        Equal( "completed", state.State );
        Equal( "alpha beta", string.Join( ' ', state.VerifiedNames ) );
        Equal( string.Empty, string.Join( ' ', state.FailedNames ) );
        Equal( "queued running verifying completed", string.Join( ' ', fixture.Progress.Select( static entry => entry.State ) ) );

        True( fixture.Progress.All( entry => string.Equals( entry.OperationId, operationId, StringComparison.Ordinal ) ) );
        True( fixture.Progress.All( static entry => !entry.Message.Contains( '/', StringComparison.Ordinal ) ) );
    }

    private static async Task HelperRemovesThroughLiteralArgvAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.PrintOutput = "alpha|1.0\n";
        fixture.Pacman.QueryOutput = string.Empty;
        var operationId = await fixture.CommitAsync( PackageOperations.Remove, ["alpha"] );
        await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
        Equal( "/usr/bin/pacman -Rs --print --print-format %n|%v -- alpha", fixture.Pacman.Invocations[0] );
        Equal( "/usr/bin/pacman -Rns --noconfirm --noprogressbar --color never -- alpha", fixture.Pacman.Invocations[3] );

        var state = fixture.Core.GetOperation( operationId );
        Equal( "completed", state.State );
        Equal( "alpha", string.Join( ' ', state.VerifiedNames ) );
    }

    private static async Task HelperFailsWhenPostVerificationDisagreesAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.PrintOutput = "alpha|1.0|core\nbeta|2.0|extra\n";
        fixture.Pacman.QueryOutput = "alpha 1.0\n";
        var operationId = await fixture.CommitAsync( PackageOperations.Install, ["alpha", "beta"] );
        await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
        var state = fixture.Core.GetOperation( operationId );
        Equal( "failed", state.State );
        Equal( "alpha", string.Join( ' ', state.VerifiedNames ) );
        Equal( "beta", string.Join( ' ', state.FailedNames ) );
        True( !state.Message.Contains( '/', StringComparison.Ordinal ) );
        using var rejected = PackageHelperFixture.Create( );
        rejected.Pacman.CommitExitCode = 1;
        var failing = await rejected.CommitAsync( PackageOperations.Install, ["alpha"] );
        await rejected.Core.RunQueuedOperationAsync( CancellationToken.None );
        var failedState = rejected.Core.GetOperation( failing );
        Equal( "failed", failedState.State );
        Equal( "alpha", string.Join( ' ', failedState.FailedNames ) );
        Equal( "queued running failed", string.Join( ' ', rejected.Progress.Select( static entry => entry.State ) ) );
    }

    private static async Task HelperPostVerificationRequiresApprovedVersionsAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.PrintOutput = "alpha|1.0|core\n";
        fixture.Pacman.QueryOutput = "alpha 9.9\n";
        var operationId = await fixture.CommitAsync( PackageOperations.Install, ["alpha"] );
        await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
        var state = fixture.Core.GetOperation( operationId );
        Equal( "failed", state.State );
        Equal( "alpha", string.Join( ' ', state.FailedNames ) );
        Equal( string.Empty, string.Join( ' ', state.VerifiedNames ) );
    }

    private static async Task HelperCancelsOnlyQueuedOperationsAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.QueryOutput = "alpha 1.0\n";
        var operationId = await fixture.CommitAsync( PackageOperations.Install, ["alpha"] );
        True( fixture.Core.Cancel( operationId ) );
        Equal( "cancelled", fixture.Core.GetOperation( operationId ).State );
        True( !fixture.Core.Cancel( operationId ) );
        var drained = await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
        True( drained is null );
        True( !fixture.Pacman.RanCommit );
        var second = await fixture.CommitAsync( PackageOperations.Install, ["alpha"] );
        await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
        Equal( "completed", fixture.Core.GetOperation( second ).State );
        True( !fixture.Core.Cancel( second ) );
        Throws<PackageHelperException>( ( ) => fixture.Core.GetOperation( "package-missing" ) );
    }

    private static async Task HelperNeverRunsAnAcceptedCancellationAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.PrintOutput = "alpha|1.0|core\n";
        fixture.Pacman.QueryOutput = "alpha 1.0\n";
        var cancellations = 0;

        // The worker and Cancel race for the same queued operation; the
        // canceller's start is staggered so the race lands at varying points
        // of the worker's dequeue and start.
        for ( var attempt = 0; attempt < 4000; attempt++ )
        {
            var operationId = await fixture.CommitAsync( PackageOperations.Install, ["alpha"] );
            var commits = fixture.Pacman.Commits;
            var delay = attempt % 200;
            using Barrier start = new( 2 );

            var worker = Task.Run( async ( ) =>
            {
                start.SignalAndWait( );
                await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
            } );

            var canceller = Task.Run( ( ) =>
            {
                start.SignalAndWait( );
                Thread.SpinWait( delay );

                return fixture.Core.Cancel( operationId );
            } );

            await Task.WhenAll( worker, canceller );
            var state = fixture.Core.GetOperation( operationId ).State;

            if ( canceller.Result )
            {
                cancellations++;
                Equal( "cancelled", state );
                Equal( commits, fixture.Pacman.Commits );
            }
            else
            {
                Equal( "completed", state );
            }
        }

        True( cancellations > 0 );
    }

    private static async Task HelperFailsRemovalWhenVerificationIsInconclusiveAsync( )
    {
        PackageProcessResult[] inconclusive = CreatePackageFixtureInconclusive( );
        foreach ( var query in inconclusive )
        {
            using var fixture = PackageHelperFixture.Create( );
            fixture.Pacman.PrintOutput = "alpha|1.0\n";
            fixture.Pacman.QueryResult = query;
            var operationId = await fixture.CommitAsync( PackageOperations.Remove, ["alpha"] );
            await fixture.Core.RunQueuedOperationAsync( CancellationToken.None );
            var state = fixture.Core.GetOperation( operationId );
            Equal( "failed", state.State );
            Equal( "alpha", string.Join( ' ', state.FailedNames ) );
            Equal( string.Empty, string.Join( ' ', state.VerifiedNames ) );
            True( fixture.Pacman.RanCommit );
        }
    }

    private static async Task<PackageHelperException> CaptureAsync( Func<Task<string>> action )
    {
        try
        {
            await action( );
        }
        catch ( PackageHelperException exception )
        {
            return exception;
        }

        throw new InvalidOperationException( "Expected a PackageHelperException." );
    }
}

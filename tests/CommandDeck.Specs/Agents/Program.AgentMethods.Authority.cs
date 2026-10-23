using System.Text;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void AgentCredentialStoreOwnsAPrivateFile( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-agent-cred-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            CodexCredentialStore store = new( Path.Combine( root, "state" ) );
            True( store is { Exists: false } && store.Read( ) is null );
            Throws<InvalidOperationException>( ( ) => store.Write( "not json"u8 ) );
            Throws<InvalidOperationException>( ( ) => store.Write( "{\"unrelated\":1}"u8 ) );
            store.Write( """{"tokens":{"access_token":"a","refresh_token":"b","account_id":"c"}}"""u8 );
            True( store.Exists );

            if ( OperatingSystem.IsLinux( ) )
            {
                var mode = File.GetUnixFileMode( store.FilePath );

                Equal( UnixFileMode.None, mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite) );
            }

            var read = store.Read( );

            True( read is not null &&
                  Encoding
                      .UTF8.GetString( read )
                      .Contains( "refresh_token", StringComparison.Ordinal ) );

            using ( var home = CodexRuntimeHome.Create( Path.Combine( root, "runtime" ), AgentTrustLevels.ApprovedContext, webSearch: false ) )
            {
                home.ImportCredential( read );
                CodexCredentialStore.Clear( read );
                True( read!.All( static value => value == 0 ) );
                True( File.Exists( home.AuthPath ) );
                True( File.Exists( Path.Combine( home.Path, "config.toml" ) ) );
                var exported = home.ExportCredential( );
                True( exported is not null && exported.Length == read!.Length );
                CodexCredentialStore.Clear( exported );
                True( exported!.All( static value => value == 0 ) );
                var path = home.Path;
                home.Wipe( );
                True( !Directory.Exists( path ) );
            }

            store.Delete( );
            True( !store.Exists );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task AgentCommandToolPlansLiteralArgvAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-agent-cmd-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            AgentCommandTool tool = new( root );
            var plan = tool.Plan( ["/usr/bin/printf", "hello %s\n", "world"], null );
            Equal( "/usr/bin/printf", plan.Executable );
            Equal( root, plan.WorkingDirectory );
            True( plan is { ExecutableIdentity.Length: > 0, Digest.Length: 64 } );
            Equal( "/usr/bin:/bin", plan.Environment["PATH"] );
            Equal( "C.UTF-8", plan.Environment["LANG"] );
            Equal( "C.UTF-8", plan.Environment["LC_ALL"] );

            var changedEnvironmentDigest = AgentCommandTool.ComputeDigest( plan.ExecutableIdentity, plan.Argv, plan.WorkingDirectory, plan.EnvironmentProfile,
                new Dictionary<string, string>( plan.Environment, StringComparer.Ordinal ) { ["PATH"] = "/tmp/user-writable:/usr/bin:/bin" } );

            True( changedEnvironmentDigest != plan.Digest );
            var same = tool.Plan( ["printf", "hello %s\n", "world"], root );
            Equal( plan.Digest, same.Digest );
            var different = tool.Plan( ["printf", "hello %s\n", "there"], root );
            True( different.Digest != plan.Digest );
            Throws<InvalidOperationException>( ( ) => tool.Plan( ["definitely-missing-binary-xyz"], null ) );
            Throws<InvalidOperationException>( ( ) => tool.Plan( ["./relative"], null ) );
            Throws<InvalidOperationException>( ( ) => tool.Plan( ["printf"], Path.Combine( root, "missing" ) ) );

            var outcome = await AgentCommandTool.ExecuteAsync( plan, default );
            Equal( 0, outcome.ExitCode );
            True( outcome.Output.StartsWith( "hello world", StringComparison.Ordinal ) );
            var escaped = AgentCommandTool.EscapeTerminalControls( "a" + ( char ) 27 + "b\n" );
            Equal( "a" + ( char ) 0xFFFD + "b\n", escaped );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task AgentTrustServiceBindsChallengesAndLeasesAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-agent-trust-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( root );

        try
        {
            AdjustableTimeProvider clock = new( new DateTimeOffset( 2026, 9, 2, 12, 0, 0,
                TimeSpan.Zero ) );

            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) }, clock );

            await catalog.InitializeAsync( default );
            AgentAuthority authority = new( catalog.AgentPreferences, clock );
            AgentTrustService trust = new( authority, clock );

            var current = await catalog.AgentPreferences.SetAgentPreferencesAsync( new AgentPreferences { Enabled = true }, default );

            var challenge = trust.CreateChallenge( "client-a", AgentTrustLevels.Unrestricted, current );
            True( challenge is { RequiresPhrase: true, RequiresDuration: true, Warnings.Count: >= 8 } );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await trust.ApplyAsync( "client-b", challenge.Id, AgentTrustLevels.Unrestricted,
                AgentLimits.UnrestrictedPhrase, AgentTrustDurations.OneHour, default ) );

            var second = trust.CreateChallenge( "client-a", AgentTrustLevels.Unrestricted, current );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await trust.ApplyAsync( "client-a", second.Id, AgentTrustLevels.Unrestricted,
                "allow unrestricted agent", AgentTrustDurations.OneHour, default ) );

            var third = trust.CreateChallenge( "client-a", AgentTrustLevels.Unrestricted, current );

            var applied = await trust.ApplyAsync( "client-a", third.Id, AgentTrustLevels.Unrestricted, AgentLimits.UnrestrictedPhrase,
                AgentTrustDurations.OneHour, default );

            Equal( 6, applied.TrustLevel );

            True( applied.TrustLeaseExpiresUnixMilliseconds ==
                  clock
                      .GetUtcNow( )
                      .AddMinutes( 60 )
                      .ToUnixTimeMilliseconds( ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await trust.ApplyAsync( "client-a", third.Id, AgentTrustLevels.Unrestricted,
                AgentLimits.UnrestrictedPhrase, AgentTrustDurations.OneHour, default ) );

            var stillValid = (await authority.LoadAsync( default )).Snapshot.Preferences;
            Equal( 6, stillValid.TrustLevel );
            clock.Advance( TimeSpan.FromMinutes( 61 ) );
            var expired = (await authority.LoadAsync( default )).Snapshot.Preferences;
            Equal( 2, expired.TrustLevel );
            var stale = trust.CreateChallenge( "client-a", AgentTrustLevels.Packages, expired );
            clock.Advance( TimeSpan.FromSeconds( AgentLimits.ChallengeLifetimeSeconds + 1 ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await trust.ApplyAsync( "client-a", stale.Id, AgentTrustLevels.Packages, null, null, default ) );

            var described = AgentTrustService.Describe( expired );
            Equal( "Approved context", described.LevelName );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}

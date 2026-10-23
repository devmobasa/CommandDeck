namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AgentSocketTrustChallengesAndDisableResetAuthorityAsync( )
    {
        ScriptedAgentBackend backend = new( );
        using CancellationTokenSource serverCancellation = new( );

        (var runtime, var _, var root, var _) = await StartAgentRuntimeAsync( backend, serverCancellation.Token );

        try
        {
            await using ( runtime )
            {
                await using var client = await ConnectWithRetryAsync( SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" ) );

                await client.SendAsync(
                    new ProtocolRequest
                    {
                        RequestId = 1,
                        Operation = ProtocolOperations.AgentPreferencesUpdate,
                        AgentPreferences = new AgentPreferences { Enabled = true, TrustLevel = AgentTrustLevels.Unrestricted }
                    }, default );

                var forged = await CollectUntilDoneAsync( client, 1 );
                True( forged[0].Type == "error" );

                await client.SendAsync(
                    new ProtocolRequest
                    {
                        RequestId = 2,
                        Operation = ProtocolOperations.AgentPreferencesUpdate,
                        AgentPreferences = new AgentPreferences { Enabled = true }
                    }, default );

                await CollectUntilDoneAsync( client, 2 );

                await client.SendAsync(
                    new ProtocolRequest { RequestId = 3, Operation = ProtocolOperations.AgentTrustChallenge, TrustLevel = AgentTrustLevels.ConfirmedCommands },
                    default );

                var challenged = await CollectUntilDoneAsync( client, 3 );
                var challenge = challenged[0].AgentTrustChallenge!;
                True( !challenge.RequiresPhrase );

                await client.SendAsync( new ProtocolRequest
                {
                    RequestId = 4,
                    Operation = ProtocolOperations.AgentTrustUpdate,
                    ChallengeId = challenge.Id,
                    TrustLevel = AgentTrustLevels.ConfirmedCommands
                }, default );

                var applied = await CollectUntilDoneAsync( client, 4 );
                Equal( 5, applied[0].AgentTrust?.Level );

                await client.SendAsync( new ProtocolRequest
                {
                    RequestId = 5,
                    Operation = ProtocolOperations.AgentTrustUpdate,
                    ChallengeId = challenge.Id,
                    TrustLevel = AgentTrustLevels.ConfirmedCommands
                }, default );

                var replayed = await CollectUntilDoneAsync( client, 5 );
                True( replayed[0].Type == "error" );

                await client.SendAsync( new ProtocolRequest
                {
                    RequestId = 6,
                    Operation = ProtocolOperations.AgentGrantsAdd,
                    GrantKind = AgentGrantKinds.Folder,
                    GrantScope = root
                }, default );

                var grantsAfterAdd = await CollectUntilDoneAsync( client, 6 );
                True( grantsAfterAdd[0].Type == "error" || grantsAfterAdd[0].AgentGrants is not null );

                await client.SendAsync( new ProtocolRequest { RequestId = 7, Operation = ProtocolOperations.AgentStatusGet }, default );

                var status = await CollectUntilDoneAsync( client, 7 );
                Equal( 5, status[0].AgentStatus?.Trust.Level );
                True( status[0].AgentStatus?.Helper.Installed == false );

                await client.SendAsync(
                    new ProtocolRequest
                    {
                        RequestId = 8,
                        Operation = ProtocolOperations.AgentPreferencesUpdate,
                        AgentPreferences = new AgentPreferences { Enabled = false, TrustLevel = AgentTrustLevels.ConfirmedCommands }
                    }, default );

                var disabled = await CollectUntilDoneAsync( client, 8 );
                Equal( 2, disabled[0].AgentPreferences?.TrustLevel );
                True( backend.Stopped );

                await client.SendAsync( new ProtocolRequest { RequestId = 9, Operation = ProtocolOperations.AgentGrantsGet }, default );

                var grants = await CollectUntilDoneAsync( client, 9 );
                Equal( 0, grants[0].AgentGrants?.Grants.Count );

                await client.SendAsync( new ProtocolRequest
                {
                    RequestId = 12,
                    Operation = ProtocolOperations.AgentTurnStart,
                    Query = "x",
                    Version = 2
                }, default );

                var oldVersion = await CollectUntilDoneAsync( client, 12 );

                True( oldVersion.Any( message => message.Type == "error" && message.Error!.Contains( "version", StringComparison.OrdinalIgnoreCase ) ) );
            }
        }
        finally
        {
            serverCancellation.Cancel( );
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task AgentLoginPageOpensThroughCachedActionAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        ScriptedAgentBackend backend = new( ) { Authenticated = false };
        using CancellationTokenSource serverCancellation = new( );
        var (runtime, _, root, _) = await StartAgentRuntimeAsync( backend, serverCancellation.Token );
        var originalPath = Environment.GetEnvironmentVariable( "PATH" );

        try
        {
            await using ( runtime )
            {
                await using var client = await ConnectWithRetryAsync( SpecTemporaryFiles.SocketPath( root, "commanddeck.sock" ) );

                await client.SendAsync(
                    new ProtocolRequest
                    {
                        RequestId = 1,
                        Operation = ProtocolOperations.AgentPreferencesUpdate,
                        AgentPreferences = new AgentPreferences { Enabled = true }
                    }, default );

                _ = await CollectUntilDoneAsync( client, 1 );

                await client.SendAsync(
                    new ProtocolRequest { RequestId = 2, Operation = ProtocolOperations.AgentLoginStart, LoginMethod = AgentLoginMethods.DeviceCode },
                    default );

                var pending = (await CollectUntilDoneAsync( client, 2 )).Single( static message => message.AgentLogin is not null ).AgentLogin!;

                Equal( "pending", pending.State );
                var capturedArguments = Path.Combine( root, "browser-arguments" );
                var launcher = Path.Combine( root, "uwsm-app" );

                await File.WriteAllTextAsync( launcher, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{capturedArguments}'\n" );

                File.SetUnixFileMode( launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );

                Environment.SetEnvironmentVariable( "PATH", root );

                await client.SendAsync( new ProtocolRequest
                {
                    RequestId = 3,
                    Operation = ProtocolOperations.Invoke,
                    ResultId = AgentResultIds.Login,
                    InvocationReference = pending.InvocationReference,
                    ActionId = AgentLoginActionIds.OpenUrl
                }, default );

                var action = (await CollectUntilDoneAsync( client, 3 )).Single( static message => message.ActionResult is not null ).ActionResult!;

                True( action.Succeeded, "the login page action starts the desktop browser" );
                var arguments = await File.ReadAllLinesAsync( capturedArguments );
                Equal( "-t|service|-S|both|--|xdg-open", string.Join( '|', arguments.Take( 6 ) ) );
                Equal( pending.VerificationUrl, arguments[6] );
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable( "PATH", originalPath );
            serverCancellation.Cancel( );
            Directory.Delete( root, recursive: true );
        }
    }
}

using System.Text;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void ExtensionBrokerAuthorityRejectsForeignAndRetiredRequests( )
    {
        var authority = new ExtensionViewAuthority( "session", 1, 3 );
        var invocation = authority.BeginInvocation( InvocationOrigin.Open );
        var request = new BrokerRequest( "session", 1, 1, invocation.Id, ExtensionBrokerOperations.ListFiles, "notes" );
        var grants = new HashSet<string> { ExtensionPermissionIds.FilesRead };
        authority.AuthorizeBroker( request, 3, grants );

        foreach ( var invalid in new[]
                 {
                     request with { SessionId = "foreign" }, request with { ExecutionEpoch = 2 }, request with { Generation = 2 },
                     request with { InvocationId = "foreign" }
                 } )
        {
            Throws<InvalidOperationException>( ( ) => authority.AuthorizeBroker( invalid, 3, grants ) );
        }

        Throws<InvalidOperationException>( ( ) => authority.AuthorizeBroker( request, 4, grants ) );
        Throws<InvalidOperationException>( ( ) => authority.AuthorizeBroker( request with { Operation = ExtensionBrokerOperations.SendHttp }, 3, grants ) );

        authority.CompleteInvocation( invocation );
        Throws<InvalidOperationException>( ( ) => authority.AuthorizeBroker( request, 3, grants ) );
        authority.BeginInvocation( InvocationOrigin.UserAction );
        authority.Revoke( );

        Throws<InvalidOperationException>( ( ) => authority.AuthorizeBroker( request with { Generation = 2 }, 3, grants ) );
    }

    private static async Task ExtensionFileConnectionsRejectReplacementLinksAndRevocationAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var folder = Path.Combine( root, "notes" );
            Directory.CreateDirectory( folder );
            var file = Path.Combine( folder, "note.txt" );
            await File.WriteAllTextAsync( file, "hello" );
            await File.WriteAllTextAsync( Path.Combine( folder, "secrets.json" ), "protected" );
            File.CreateSymbolicLink( Path.Combine( folder, "linked.txt" ), file );
            Directory.CreateSymbolicLink( Path.Combine( root, "alias" ), folder );

            using var broker = new ExtensionConnections( Path.Combine( root, "journal" ), new ExtensionSecretResolver( ) );

            await broker.ConfigureAsync( "package", new( ) { Name = "notes", Path = folder }, CancellationToken.None );

            async Task<BrokerResult> Access( BrokerRequest input, string package = "package" ) =>
                await broker.ExecuteAsync( package, input, false, ( _, _ ) => ValueTask.FromResult( false ), CancellationToken.None );

            var list = new BrokerRequest( "s", 1, 1, "i", ExtensionBrokerOperations.ListFiles, "notes" );
            var result = await Access( list );
            Equal( 1, result.Entries!.Length );

            var read = list with { Operation = ExtensionBrokerOperations.ReadFile, Reference = result.Entries[0].Reference };

            Equal( "hello", Encoding.UTF8.GetString( Convert.FromBase64String( (await Access( read )).Body! ) ) );

            await broker.ConfigureAsync( "package", new( ) { Name = "single", Path = file }, CancellationToken.None );

            var single = await Access( list with { Connection = "single" } );
            Equal( 1, single.Entries!.Length );

            Equal( "hello",
                Encoding.UTF8.GetString(
                    Convert.FromBase64String( (await Access( read with { Connection = "single", Reference = single.Entries[0].Reference } )).Body! ) ) );

            var binary = Enumerable
                .Repeat( ( byte ) 251, 128 * 1024 )
                .ToArray( );

            var binaryPath = Path.Combine( root, "binary.bin" );
            await File.WriteAllBytesAsync( binaryPath, binary );

            await broker.ConfigureAsync( "package", new( ) { Name = "binary", Path = binaryPath }, CancellationToken.None );

            var binaryEntry = (await Access( list with { Connection = "binary" } )).Entries![0];

            var binaryResult = await Access( read with { Connection = "binary", Reference = binaryEntry.Reference } );

            var frame = ExtensionFraming.Serialize( new( )
            {
                Id = "e:binary",
                Result = JsonSerializer.SerializeToElement( binaryResult, BrokerJsonContext.Default.BrokerResult )
            } );

            True( frame.Length <= ExtensionLimits.MaximumFrameBytes );
            using var wire = new MemoryStream( frame );

            var roundTrip =
                (await new ExtensionFraming( wire ).ReadAsync( CancellationToken.None ))!.Result!.Value.Deserialize( BrokerJsonContext.Default.BrokerResult )!;

            True( binary.SequenceEqual( Convert.FromBase64String( roundTrip.Body! ) ) );

            var requestFrame = ExtensionFraming.Serialize( new( )
            {
                Id = "e:request",
                Method = ExtensionMethods.CommandBroker,
                Params = JsonSerializer.SerializeToElement(
                    list with
                    {
                        Operation = ExtensionBrokerOperations.SendHttp,
                        Http = new( "POST", "/tasks", Convert.ToBase64String( binary.AsSpan( 0, 64 * 1024 ) ) )
                    }, BrokerJsonContext.Default.BrokerRequest )
            } );

            True( requestFrame.Length <= ExtensionLimits.MaximumFrameBytes );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await Access( read, "other" ) );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await Access( read with { Reference = file } ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await broker.ConfigureAsync( "package", new( ) { Name = "alias", Path = Path.Combine( root, "alias" ) }, CancellationToken.None ) );

            var nested = Path.Combine( folder, "nested" );
            Directory.CreateDirectory( nested );
            await File.WriteAllTextAsync( Path.Combine( nested, "inside.txt" ), "inside" );
            var directory = (await Access( list )).Entries!.Single( entry => entry.Directory );
            Directory.Move( nested, nested + "-moved" );
            Directory.CreateSymbolicLink( nested, nested + "-moved" );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await Access( list with { Reference = directory.Reference } ) );

            await ThrowsAsync<InvalidOperationException>( async ( ) =>
                await broker.ConfigureAsync( "package", new( ) { Name = "protected", Path = Path.Combine( folder, "secrets.json" ) },
                    CancellationToken.None ) );

            File.Move( file, file + ".old" );
            await File.WriteAllTextAsync( file, "replacement" );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await Access( read ) );
            Directory.Move( folder, folder + "-moved" );
            Directory.CreateDirectory( folder );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await Access( list ) );
            broker.Revoke( "package" );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await Access( list ) );
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }
}

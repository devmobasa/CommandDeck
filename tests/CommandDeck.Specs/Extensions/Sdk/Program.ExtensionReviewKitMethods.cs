using System.Text.Json;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions;
using CommandDeck.Extensions.Testing;
using CommandDeck.Extensions.Views;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task ExtensionKitValidatesStorageKeySyntaxAsync( )
    {
        foreach ( var key in new[] { "bad key", "/absolute", "λ", "", new string( 'x', 65 ) } )
        {
            KitStorageKeyCommand command = new( key );
            await using var host = await ExtensionTestHost.StartAsync( command );
            await host.OpenAsync( );
            Equal( "invalid_request", command.ErrorCode );
            Equal( 0L, command.Stored!.Revision );
            Equal( 0, command.Stored.Values.Count );
        }

        KitStorageKeyCommand allowed = new( "good-key_1.txt" );
        await using var valid = await ExtensionTestHost.StartAsync( allowed );
        await valid.OpenAsync( );
        True( allowed.ErrorCode is null );
        Equal( 1L, allowed.Stored!.Revision );

        Equal( "value", allowed
            .Stored.Values["good-key_1.txt"]
            .GetString( ) );
    }

    private static async Task ExtensionKitRequiresCheckedCheckboxAsync( )
    {
        ReviewFormCommand command = new( );
        await using var host = await ExtensionTestHost.StartAsync( command );
        await host.OpenAsync( );

        await ThrowsAsync<InvalidDataException>( async ( ) =>
            await host.SubmitAsync( new Dictionary<string, JsonElement> { ["accepted"] = JsonElement.Parse( "false" ) } ) );

        Equal( 0, command.Submissions );

        await host.SubmitAsync( new Dictionary<string, JsonElement> { ["accepted"] = JsonElement.Parse( "true" ) } );

        Equal( 1, command.Submissions );
    }

    private static async Task ExtensionKitFilePagesMatchProductionAsync( )
    {
        var root = CreateCliSpecRoot( );

        try
        {
            var files = new Dictionary<string, byte[]>( StringComparer.Ordinal );
            for ( var index = 0; index < 65; index++ )
            {
                var name = $"note-{index:D2}.txt";
                files.Add( name, [] );
                await File.WriteAllBytesAsync( Path.Combine( root, name ), [] );
            }

            KitFilePagesCommand command = new( );
            await using var host = await ExtensionTestHost.StartAsync( command, new( ) { Files = new( ) { ["notes"] = files } } );
            await host.OpenAsync( );
            Equal( "32,32,1", string.Join( ',', command.Pages.Select( page => page.Entries.Length ) ) );
            Equal( 32, command.Pages[0].NextOffset );
            Equal( 64, command.Pages[1].NextOffset );
            True( command.Pages[2].NextOffset is null );
            using var production = new ExtensionFileRoot( root );
            var offset = 0;
            foreach ( var page in command.Pages )
            {
                var actual = production.Access( new FileListOperation( null, offset ), CancellationToken.None );
                Equal( string.Join( ',', page.Entries.Select( entry => entry.Name ) ), string.Join( ',', actual.Entries!.Select( entry => entry.Name ) ) );
                Equal( page.NextOffset, actual.NextOffset );
                offset = page.NextOffset ?? 0;
            }
        }
        finally
        {
            Directory.Delete( root, true );
        }
    }

    private sealed class KitFilePagesCommand : ExtensionCommandBase
    {
        public override string Id => "kit-pages";
        public List<FileListing> Pages { get; } = [];

        public override async ValueTask OpenAsync( CommandContext context, CancellationToken cancellationToken )
        {
            var offset = 0;
            do
            {
                var page = await context.Brokers.ListAsync( "notes", offset: offset, cancellationToken: cancellationToken );
                Pages.Add( page );
                offset = page.NextOffset ?? 0;
            } while ( offset != 0 );

            await context.Views.ReplaceAsync( new DetailView { Id = "pages", Title = "Pages" }, cancellationToken );
        }
    }

    private sealed class KitStorageKeyCommand( string key ) : IExtensionCommand
    {
        public string Id => "kit-storage-key";
        public string? ErrorCode { get; private set; }
        public CommandDataResult? Stored { get; private set; }

        public async ValueTask OpenAsync( CommandContext context, CancellationToken cancellationToken )
        {
            try
            {
                await context.Data.WriteAsync( key, JsonElement.Parse( "\"value\"" ), 0, cancellationToken );
            }
            catch ( ExtensionRpcException exception )
            {
                ErrorCode = exception.ErrorCode;
            }

            Stored = await context.Data.ReadAsync( cancellationToken );

            await context.Views.ReplaceAsync( new DetailView { Id = "kit-storage-key", Title = "Storage" }, cancellationToken );
        }

        public ValueTask HandleAsync( CommandContext context, CommandEvent commandEvent, CancellationToken cancellationToken ) => ValueTask.CompletedTask;
    }
}

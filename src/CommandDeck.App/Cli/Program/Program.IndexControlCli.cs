using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private static async Task<int> RunIndexControlCliAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        var action = ReadCliAction( arguments.PositionalsAfterCommand( ), 0, StatusCommand );
        var operation = action switch
        {
            PauseCommand => ProtocolOperations.IndexPause,
            ResumeCommand => ProtocolOperations.IndexResume,
            StatusCommand => ProtocolOperations.Status,
            _ => null
        };
        if ( operation is null )
        {
            Console.Error.WriteLine( "Use 'index status', 'index pause', or 'index resume'." );

            return 2;
        }

        var client = await ConnectPreferenceDaemonAsync( arguments, cancellationToken )
            .ConfigureAwait( false );
        if ( client is null )
        {
            return await RunWithRuntimeAsync( arguments, cancellationToken )
                .ConfigureAwait( false );
        }

        await using ( client )
        {
            var request = new ProtocolRequest { RequestId = 1, Operation = operation, RootId = arguments.GetOption( CliOptionNames.Root ) };
            await client.SendAsync( request, cancellationToken )
                .ConfigureAwait( false );

            IncrementalIndexStatus? status = null;
            await foreach ( var line in client.ReadRawMessagesAsync( cancellationToken ).ConfigureAwait( false ) )
            {
                var message = JsonSerializer.Deserialize( line, CommandDeckJsonContext.Default.ProtocolMessage );
                if ( message?.Type == ProtocolMessageTypes.Error )
                {
                    throw new InvalidOperationException( message.Error );
                }

                status ??= message?.RuntimeStatus?.Indexing;
                if ( message?.Type == ProtocolMessageTypes.Done )
                {
                    if ( status is null )
                    {
                        throw new InvalidOperationException( "The daemon did not return indexing status." );
                    }

                    return WriteIndexControlStatus( status );
                }
            }
        }

        throw new InvalidOperationException( "The daemon disconnected before returning indexing status." );
    }
}

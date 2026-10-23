namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task VerifyRuntimeStatusProtocolAsync( UnixProtocolClient client, CancellationToken cancellationToken )
    {
        const long requestId = 390;

        await client.SendAsync( new ProtocolRequest { RequestId = requestId, Operation = ProtocolOperations.Status }, cancellationToken );

        ProtocolMessage? status = null;
        var done = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        timeout.CancelAfter( TimeSpan.FromSeconds( 5 ) );

        await foreach ( var line in client.ReadRawMessagesAsync( timeout.Token ) )
        {
            var message = DeserializeMessage( line );
            if ( message?.RequestId != requestId )
            {
                continue;
            }

            if ( message.Type == "status" )
            {
                status = message;
            }

            if ( message.Type == "done" )
            {
                done = true;

                break;
            }
        }

        True( done );
        True( status?.RuntimeStatus is not null );
        True( status!.RuntimeStatus!.Catalog.ItemCount > 0 );
        Equal( "4", status.RuntimeStatus.Indexing.ExtractorVersion );
        Equal( 1, status.RuntimeStatus.Indexing.Roots.Count );
        True( status.RuntimeStatus.Indexing.Maintenance.Enabled );
        True( status.RuntimeStatus.Indexing.Maintenance.NextRunAt is not null );
        True( status.RuntimeStatus.Inspection.DetailedWorkStateAvailable );
        True( status.RuntimeStatus.Providers.Any( provider => provider is { Id: "catalog", State: "available" } ) );
        True( status.RuntimeStatus.Providers.Any( provider => provider.Id == "hyprland" ) );
        True( status.RuntimeStatus.Providers.Any( provider => provider is { Id: "herdr", State: "available" or "inactive" or "degraded" } ) );
        True( status.RuntimeStatus.Providers.Any( provider => provider is { Id: "ai", State: "disabled" } ) );
    }
}

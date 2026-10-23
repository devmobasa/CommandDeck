namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task VerifyMultibyteFrameOverflowRecoveryAsync( UnixProtocolClient client )
    {
        string oversized = new( 'é', (ProtocolLimits.MaximumRequestBytes / 2) + 1 );
        await client.SendRawAsync( oversized, CancellationToken.None );

        await client.SendAsync( new ProtocolRequest { RequestId = 120, Operation = ProtocolOperations.Ping }, CancellationToken.None );

        var rejectedByByteCount = false;
        var nextFrameSucceeded = false;
        using CancellationTokenSource timeout = new( TimeSpan.FromSeconds( 10 ) );
        await foreach ( var line in client.ReadRawMessagesAsync( timeout.Token ) )
        {
            var message = DeserializeMessage( line );
            if ( message is { RequestId: 0, Type: "error" } )
            {
                rejectedByByteCount = message.Error?.Contains( "byte UTF-8 limit", StringComparison.Ordinal ) == true;
            }
            else if ( message is { RequestId: 120, Type: "pong" } )
            {
                nextFrameSucceeded = true;
            }
            else if ( message is { RequestId: 120, Type: "done" } )
            {
                break;
            }
        }

        True( rejectedByByteCount );
        True( nextFrameSucceeded );
    }

    private static async Task VerifyLegacyCustomizationOverflowProtocolAsync( UnixProtocolClient client, string database, CancellationToken cancellationToken )
    {
        await using ( Microsoft.Data.Sqlite.SqliteConnection connection = new( $"Data Source={database}" ) )
        {
            await connection.OpenAsync( cancellationToken );

            await using var transaction = ( Microsoft.Data.Sqlite.SqliteTransaction ) await connection.BeginTransactionAsync( cancellationToken );
            for ( var index = 0; index < 255; index++ )
            {
                await using var insert = connection.CreateCommand( );
                insert.Transaction = transaction;

                insert.CommandText = """
                                     INSERT INTO favorites(
                                         stable_id, position, title_snapshot, source_id,
                                         created_unix_ms, updated_unix_ms)
                                     VALUES($id, $position, $title, 'files', 1, 1);
                                     """;

                insert.Parameters.AddWithValue( "$id", $"file:legacy-protocol-{index:D3}" );
                insert.Parameters.AddWithValue( "$position", index + 3 );
                insert.Parameters.AddWithValue( "$title", $"Legacy protocol {index:D3}" );
                await insert.ExecuteNonQueryAsync( cancellationToken );
            }

            await transaction.CommitAsync( cancellationToken );
        }

        await client.SendAsync( new ProtocolRequest { RequestId = 110, Operation = ProtocolOperations.PreferencesGet }, CancellationToken.None );

        var boundedPreferences = await ReadMessageAsync( client, 110, "preferences-result", cancellationToken );

        ArgumentNullException.ThrowIfNull( boundedPreferences?.Preferences );

        Equal( 256, boundedPreferences.Preferences.Customizations.Count );
        Equal( 257, boundedPreferences.Preferences.TotalCustomizationCount );
        True( boundedPreferences.Preferences.CustomizationsTruncated );

        await client.SendAsync( new ProtocolRequest { RequestId = 111, Operation = ProtocolOperations.CustomizationsExport }, CancellationToken.None );

        var legacyExport = await ReadMessageAsync( client, 111, "error", cancellationToken );
        True( legacyExport?.Error?.Contains( "preferences export", StringComparison.Ordinal ) == true );
    }
}

using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const string EditCommand = "edit";

    private static async Task<int> RunQuicklinksAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var values = arguments.PositionalsAfterCommand( );
        var action = ReadCliAction( values, 0, ListCommand );
        QuicklinksResult result;
        if ( action == ListCommand && values.Count <= 1 )
        {
            result = await runtime
                .GetQuicklinksAsync( cancellationToken )
                .ConfigureAwait( false );
        }
        else if ( action == "restore" && values.Count == 2 )
        {
            var backup = await ReadQuicklinkBackupAsync( values[1], cancellationToken )
                .ConfigureAwait( false );

            result = await runtime
                .RestoreQuicklinksAsync( backup, cancellationToken )
                .ConfigureAwait( false );
        }
        else if ( IsQuicklinkSaveCommand( values, action ) )
        {
            QuicklinkOptions link = new( )
            {
                Id = GetQuicklinkId( values, action ),
                Title = arguments.GetOption( CliOptionNames.Title ) ?? string.Empty,
                UrlTemplate = arguments.GetOption( CliOptionNames.Url ) ?? string.Empty,
                Alias = arguments.GetOption( CliOptionNames.Alias ),
                Icon = arguments.GetOption( CliOptionNames.Icon )
            };

            result = await runtime
                .ChangeQuicklinkAsync( QuicklinkStoreActions.Save, link, null, cancellationToken )
                .ConfigureAwait( false );
        }
        else if ( action is QuicklinkStoreActions.Remove or QuicklinkStoreActions.Disable or QuicklinkStoreActions.Enable && values.Count == 2 )
        {
            result = await runtime
                .ChangeQuicklinkAsync( action, null, values[1], cancellationToken )
                .ConfigureAwait( false );
        }
        else
        {
            Console.Error.WriteLine(
                "Use 'quicklinks', 'quicklinks add --title TITLE --url URL [--alias ALIAS] [--icon ICON]', 'quicklinks edit ID --title TITLE --url URL', 'quicklinks remove|disable|enable ID', or 'quicklinks restore BACKUP.json'." );

            return 2;
        }

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( result, CommandDeckJsonContext.Default.QuicklinksResult ) );
        }
        else
        {
            if ( result.Message is not null )
            {
                Console.WriteLine( result.Message );
            }

            foreach ( var entry in result.Quicklinks )
            {
                var link = entry.Quicklink;

                Console.WriteLine( PreferencesTextFormatter.EscapeTerminalText(
                    $"{link.Id}\t{entry.Source}\t{(entry.Enabled ? EnabledValue : DisabledValue)}\t{link.Title}\t{link.UrlTemplate}\t{link.Alias}" ) );
            }
        }

        return 0;
    }

    private static async ValueTask<CustomizationExport> ReadQuicklinkBackupAsync( string path, CancellationToken cancellationToken )
    {
        using var input = SafeRegularFile.Open( path );
        var bytes = new byte[ProtocolLimits.MaximumCustomizationExportBytes + 1];

        var count = await input
            .ReadPrefixAsync( bytes, cancellationToken )
            .ConfigureAwait( false );

        if ( count > ProtocolLimits.MaximumCustomizationExportBytes )
        {
            throw new InvalidOperationException( "Customization backup is too large." );
        }

        var backup = JsonSerializer.Deserialize( bytes.AsSpan( 0, count ), CommandDeckJsonContext.Default.CustomizationExport );
        if ( backup is null )
        {
            throw new InvalidOperationException( "Missing customization backup." );
        }

        return backup;
    }

    private static bool IsQuicklinkSaveCommand( IReadOnlyList<string> values, string action )
    {
        return action == AddCommand && values.Count == 1 || action == EditCommand && values.Count == 2;
    }

    private static string GetQuicklinkId( IReadOnlyList<string> values, string action )
    {
        if ( action == EditCommand )
        {
            return values[1];
        }
        else
        {
            return QuicklinkIdentity.UserQuicklinkPrefix +
                   Guid
                       .NewGuid( )
                       .ToString( AppFormats.CompactGuid );
        }
    }
}

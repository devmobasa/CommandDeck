using System.Runtime.CompilerServices;

namespace CommandDeck;

public sealed class ConfiguredItemSource
{
    private const string CommandIdPrefix = "command:";
    private const string QuicklinkIdPrefix = "quicklink:";

    private const string DefaultCommandIcon = "utilities-terminal";
    private const string TrueValue = "true";
    private const string FalseValue = "false";
    private const string DefaultQuicklinkIcon = "internet-web-browser";

    private readonly CommandDeckOptions _options;

    public ConfiguredItemSource( CommandDeckOptions options )
    {
        _options = options;
    }

    public async IAsyncEnumerable<IndexedItem> ScanAsync( [EnumeratorCancellation] CancellationToken cancellationToken )
    {
        await Task.Yield( );
        cancellationToken.ThrowIfCancellationRequested( );

        yield return ClipboardHistoryNavigation.CreateItem( );

        foreach ( var item in ClipboardSequenceCommands.CreateItems( ) )
        {
            yield return item;
        }

        foreach ( var link in _options.Quicklinks )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            yield return CreateQuicklinkItem( link );
        }

        foreach ( var command in _options.Commands )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            yield return CreateCommandItem( command );
        }
    }

    public static IndexedItem CreateQuicklinkItem( QuicklinkOptions link )
    {
        return new IndexedItem
        {
            StableId = $"{QuicklinkIdPrefix}{link.Id}",
            Kind = ResultKinds.Quicklink,
            Title = link.Title,
            Subtitle = GetQuicklinkSubtitle( link ),
            Icon = link.Icon ?? DefaultQuicklinkIcon,
            Body = $"{link.Alias} {link.UrlTemplate}",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize( new UrlActionPayload( link.UrlTemplate, link.Alias ).ToArguments( ),
                CommandDeckJsonContext.Default.DictionaryStringString ),
            ProviderId = ProviderIds.Quicklinks,
            SourceId = SourceIds.CommandsQuicklinks
        };
    }

    private static string? GetCommandSubtitle( CustomCommandOptions command )
    {
        if ( command.Arguments.Count == 0 )
        {
            return null;
        }

        return string.Join( ' ', command.Arguments );
    }

    private static string GetQuicklinkSubtitle( QuicklinkOptions link )
    {
        if ( string.IsNullOrWhiteSpace( link.Alias ) )
        {
            return link.UrlTemplate;
        }

        return $"{link.Alias} · {link.UrlTemplate}";
    }

    private static IndexedItem CreateCommandItem( CustomCommandOptions command )
    {
        return new IndexedItem
        {
            StableId = $"{CommandIdPrefix}{command.Id}",
            Kind = ResultKinds.CustomCommand,
            Title = command.Title,
            Subtitle = GetCommandSubtitle( command ),
            Icon = command.Icon ?? DefaultCommandIcon,
            Body = string.Join( ' ', command.Keywords.Concat( command.Arguments ) ),
            PayloadJson = PayloadWriter.Create( (ActionArgumentNames.Argv, string.Join( '\u001f', command.Arguments )),
                (ActionArgumentNames.WorkingDirectory, command.WorkingDirectory),
                (ActionArgumentNames.Confirm, command.RequiresConfirmation ? TrueValue : FalseValue) ),
            ProviderId = ProviderIds.Commands,
            SourceId = SourceIds.CommandsQuicklinks
        };
    }
}

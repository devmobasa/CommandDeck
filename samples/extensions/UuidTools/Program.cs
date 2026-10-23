using System.Collections.Immutable;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions;
using CommandDeck.Extensions.Views;

await ExtensionApp.RunAsync( args, new UuidsCommand( ) );

sealed class UuidsCommand : IExtensionCommand
{
    private const string UuidsViewId = "uuids";
    private const string RegenerateActionId = "regenerate";
    private const string DetailsActionId = "details";
    private const string CopyActionId = "copy";
    private const string DetailsViewId = "uuid-details";

    private string[] _values = [];
    private int _page;
    private int _generation;
    public string Id => "uuids";

    public ValueTask OpenAsync( CommandContext context, CancellationToken cancellationToken )
    {
        _values = GenerateUuids( );
        _generation++;
        _page = 0;

        return ShowUuidsAsync( context, null, cancellationToken );
    }

    public ValueTask HandleAsync( CommandContext context, CommandEvent commandEvent, CancellationToken cancellationToken )
    {
        if ( commandEvent is ActionInvoked { ActionId: RegenerateActionId } )
        {
            return OpenAsync( context, cancellationToken );
        }

        if ( commandEvent is ActionInvoked { ActionId: DetailsActionId, ItemId: { } value } )
        {
            if ( _values.Contains( value, StringComparer.Ordinal ) )
            {
                return ShowDetailsAsync( context, value, cancellationToken );
            }
        }

        if ( commandEvent is PageRequested page )
        {
            var parts = page.Token.Split( ':' );
            if ( !TryReadCurrentPage( parts, out var index ) )
            {
                return ShowUuidsAsync( context, "This page has expired. Generate again.", cancellationToken );
            }

            _page = index;

            return ShowUuidsAsync( context, null, cancellationToken );
        }

        return ValueTask.CompletedTask;
    }

    private ValueTask ShowUuidsAsync( CommandContext context, string? error, CancellationToken cancellationToken )
    {
        return context.Views.ReplaceAsync( new ListView
        {
            Id = UuidsViewId,
            Title = $"UUIDs · Page {_page + 1} of 3",
            Search = new SearchField { Mode = SearchMode.Local },
            Error = error,
            PreviousPage = _page > 0 ? $"{_generation}:{_page - 1}" : null,
            NextPage = _page < 2 ? $"{_generation}:{_page + 1}" : null,
            Items = _values
                .Skip( _page * 8 )
                .Take( 8 )
                .Select( value => new ListItem
                {
                    Id = value,
                    Title = value,
                    Actions = [ViewAction.CopyText( CopyActionId, "Copy UUID", value ), ViewAction.Callback( DetailsActionId, "UUID details" )]
                } )
                .ToImmutableArray( ),
            Actions = [ViewAction.Callback( RegenerateActionId, "Generate again" )]
        }, cancellationToken );
    }

    private static ValueTask ShowDetailsAsync( CommandContext context, string value, CancellationToken cancellationToken )
    {
        return context.Views.PushAsync( new DetailView
        {
            Id = DetailsViewId,
            Title = "UUID details",
            Body = $"**UUID**\n`{value}`\n\nGenerated locally using .NET.",
            Metadata = [new( "Version", "4 (random)" ), new( "Format", "8-4-4-4-12 hexadecimal digits" )],
            Actions = [ViewAction.CopyText( CopyActionId, "Copy UUID", value )]
        }, cancellationToken );
    }

    private static string[] GenerateUuids( )
    {
        return Enumerable
            .Range( 0, 24 )
            .Select( _ => Guid
                .NewGuid( )
                .ToString( ) )
            .ToArray( );
    }
    private bool TryReadCurrentPage( string[] parts, out int index )
    {
        index = 0;

        return parts.Length == 2 && int.TryParse( parts[0], out var generation ) && generation == _generation &&
            int.TryParse( parts[1], out index ) && index is >= 0 and <= 2;
    }
}

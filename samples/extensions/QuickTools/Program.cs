using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions;
using CommandDeck.Extensions.Views;

await ExtensionApp.RunAsync( args, new ReferenceCommand( ), new CopyCommand( ) );

sealed class ReferenceCommand : IExtensionCommand
{
    private const string ReferenceCommandId = "reference";
    private const string UuidItemId = "uuid";
    private const string UuidAsset = "assets/uuid.png";
    private const string DetailsActionId = "details";
    private const string DetailsViewId = "details";
    private const string SpecificationActionId = "spec";

    public string Id => ReferenceCommandId;
    public ValueTask OpenAsync( CommandContext context, CancellationToken cancellationToken )
    {
        return context.Views.ReplaceAsync( new ListView
        {
            Id = "reference",
            Title = "UUID reference",
            Items = [new ListItem
            {
                Id = UuidItemId, Title = "UUID format", Subtitle = "View the packaged icon and specification", Asset = UuidAsset,
                Actions = [ViewAction.Callback( DetailsActionId, "Details" )]
            }]
        }, cancellationToken );
    }

    public ValueTask HandleAsync( CommandContext context, CommandEvent commandEvent, CancellationToken cancellationToken )
    {
        if ( commandEvent is not ActionInvoked { ActionId: DetailsActionId } )
        {
            return ValueTask.CompletedTask;
        }

        return context.Views.PushAsync( new DetailView
        {
            Id = DetailsViewId,
            Title = "UUID specification",
            Asset = UuidAsset,
            Body = "**UUIDs** identify values using 128 bits.\n\nThe icon is a packaged image. Opening the specification requires the desktop URL permission.",
            Actions = [ViewAction.OpenUrl( SpecificationActionId, "Open specification", "https://www.rfc-editor.org/rfc/rfc9562.html" )]
        }, cancellationToken );
    }
}

sealed class CopyCommand : IExtensionCommand
{
    private const string CopyCommandId = "copy";

    public string Id => CopyCommandId;
    public string Mode => ExtensionCommandModes.NoView;
    public ValueTask OpenAsync( CommandContext context, CancellationToken cancellationToken ) =>
        context.Effects.CopyTextAsync( Guid
            .NewGuid( )
            .ToString( ), cancellationToken );
    public ValueTask HandleAsync( CommandContext context, CommandEvent commandEvent, CancellationToken cancellationToken ) => ValueTask.CompletedTask;
}

using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions.Views;

namespace CommandDeck;

internal sealed class ExtensionViewRenderer( Func<string, string>? resolveAsset, Func<ExtensionActionBinding, string?, string> bind )
{
    private const int MaximumImageSourceCharacters = 128 * 1024;

    private int _budget = MaximumImageSourceCharacters;

    public RenderedExtensionView Build( ExtensionView view, int depth, RenderedExtensionView? retained )
    {
        var model = CreateModel( view, depth, retained );

        return view switch
        {
            ListView list => List( model, list, retained ),
            DetailView detail => Detail( model, detail, retained ),
            FormView form => Form( model, form ),
            _ => throw new InvalidDataException( "Invalid view." )
        };
    }

    private RenderedExtensionView Detail( RenderedExtensionView model, DetailView detail, RenderedExtensionView? retained ) => model with
    {
        Asset = resolveAsset is null ? detail.Asset : null,
        Body = detail.Body,
        Metadata = detail.Metadata,
        ImageSource = Image( detail.Asset, retained?.ImageSource, retained is not null )
    };

    private RenderedExtensionView Form( RenderedExtensionView model, FormView form ) => model with
    {
        Fields = form.Fields,
        Submit = Action( form.Submit, binding: new SubmitBinding( ) ),
        FieldErrors = form.FieldErrors,
        ValidatedFormRevision = form.ValidatedFormRevision
    };

    private RenderedExtensionView List( RenderedExtensionView model, ListView list, RenderedExtensionView? retained ) => model with
    {
        Search = list.Search,
        EmptyMessage = list.EmptyMessage,
        PreviousPage = PageHandle( list.PreviousPage ),
        NextPage = PageHandle( list.NextPage ),
        Items = RenderItems( list, retained )
    };

    private RenderedExtensionItem[] RenderItems( ListView list, RenderedExtensionView? retained ) => list
        .Items.Select( ( item, index ) => RenderItem( item, index, retained ) )
        .ToArray( );

    private RenderedExtensionItem RenderItem( ListItem item, int index, RenderedExtensionView? retained ) => new( item.Id, item.Title, item.Subtitle,
        resolveAsset is null ? item.Asset : null, RenderActions( item.Actions, item.Id ),
        Image( item.Asset, retained?.Items?[index].ImageSource, retained is not null ) );

    private RenderedExtensionAction[] RenderActions( IReadOnlyList<ViewAction> actions, string? itemId = null ) => actions
        .Select( action => Action( action, itemId ) )
        .ToArray( );

    private string? PageHandle( string? page )
    {
        if ( page is null )
        {
            return null;
        }

        return bind( new PageBinding( page ), null );
    }

    private RenderedExtensionAction Action( ViewAction action, string? itemId = null, ExtensionActionBinding? binding = null ) =>
        new( action.Id, action.Kind, action.Title, bind( binding ?? CreateBinding( action ), itemId ) );

    private static ExtensionActionBinding CreateBinding( ViewAction action ) =>
        action is CallbackAction ? new CallbackBinding( action.Id ) : new EffectBinding( action );

    private string? Image( string? asset, string? prior, bool restoring )
    {
        if ( asset is null || resolveAsset is null )
        {
            return null;
        }

        var image = restoring ? prior : resolveAsset( asset );
        _budget -= image?.Length ?? 0;

        if ( _budget < 0 )
        {
            throw new InvalidDataException( "The view's assets exceed 128 KiB." );
        }

        return image;
    }

    private static string Kind( ExtensionView view ) => view switch
    {
        ListView => ExtensionViewKinds.List,
        DetailView => ExtensionViewKinds.Detail,
        FormView => ExtensionViewKinds.Form,
        _ => throw new InvalidDataException( "Invalid view." )
    };

    private RenderedExtensionView CreateModel( ExtensionView view, int depth, RenderedExtensionView? retained ) => new( )
    {
        Kind = Kind( view ),
        Id = view.Id,
        Title = view.Title,
        Actions = RenderActions( view.Actions ),
        Loading = view.Loading,
        Error = view.Error,
        Depth = depth,
        BackHandle = BackHandle( retained is null, depth ),
        RestoredBackHandle = BackHandle( retained is not null, depth )
    };

    private string? BackHandle( bool active, int depth )
    {
        if ( !active || depth <= 1 )
        {
            return null;
        }

        return bind( new BackBinding( ), null );
    }
}

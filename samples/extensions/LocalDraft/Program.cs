using System.Text.Json;
using System.Text.Json.Serialization;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions;
using CommandDeck.Extensions.Views;
using CommandDeck.ExtensionSample;

await ExtensionApp.RunAsync( args, new DraftCommand( ) );

namespace CommandDeck.ExtensionSample
{
    public sealed class DraftCommand : ExtensionCommandBase
    {
        private const string EditCommandId = "edit";
        private const string DraftKey = "draft";
        private const string DefaultTitleSetting = "defaultTitle";
        private const string NoteCategory = "note";
        private const string SaveActionId = "save";
        private const string TitleFieldId = "title";
        private const string BodyFieldId = "body";
        private const string CategoryFieldId = "category";
        private const string TaskCategory = "task";
        private const string DoneFieldId = "done";

        private long _storageRevision;
        private FormView? _form;
        public override string Id => EditCommandId;
        public override async ValueTask OpenAsync( CommandContext context, CancellationToken cancellationToken )
        {
            var settings = await context.Data.ReadSettingsAsync( cancellationToken );
            var stored = await context.Data.ReadAsync( cancellationToken );
            _storageRevision = stored.Revision;
            var draft = stored.Values.TryGetValue( DraftKey, out var value ) ? value.Deserialize( DraftJsonContext.Default.Draft ) : null;

            draft ??= new Draft( settings.Values[DefaultTitleSetting].GetString( )!, "", NoteCategory, false );
            _form = CreateDraftForm( draft );
            await context.Views.ReplaceAsync( _form, cancellationToken );
        }

        protected override async ValueTask OnSubmitAsync( CommandContext context, FormSubmitted submission, CancellationToken cancellationToken )
        {
            if ( submission is not FormSubmitted { ActionId: SaveActionId } submitted ||
                 _form is null )
            {
                return;
            }

            var draft = new Draft( submitted.Values[TitleFieldId].GetString( )!, submitted.Values[BodyFieldId].GetString( )!,
                                submitted.Values[CategoryFieldId].GetString( )!, submitted.Values[DoneFieldId].GetBoolean( ) );
            if ( draft.Title
                .Trim( )
                .Equals( "untitled", StringComparison.OrdinalIgnoreCase ) )
            {
                await context.Views.ReplaceAsync( _form with
                {
                    FieldErrors = _form.FieldErrors.SetItem( TitleFieldId, "Choose a title other than Untitled." ),
                    ValidatedFormRevision = submitted.FormRevision
                }, cancellationToken );

                return;
            }

            try
            {
                var saved = await context.Data.WriteAsync( DraftKey, JsonSerializer.SerializeToElement( draft, DraftJsonContext.Default.Draft ), _storageRevision, cancellationToken );

                _storageRevision = saved.Revision;

                _form = _form with
                {
                    Title = "Draft saved",
                    FieldErrors = _form.FieldErrors.Clear( ),
                    ValidatedFormRevision = submitted.FormRevision,
                    Error = null
                };
            }
            catch ( ExtensionRpcException exception )
            {
                _form = _form with
                {
                    Error = exception.IsConflict ? "The draft changed elsewhere. Close and reopen before saving again." : "The draft could not be saved. Check permissions and reopen before trying again."
                };
            }

            await context.Views.ReplaceAsync( _form, cancellationToken );
        }

        private static FormView CreateDraftForm( Draft draft )
        {
            return new FormView
            {
                Id = "draft",
                Title = "Local draft",
                Submit = ViewAction.Callback( SaveActionId, "Save draft" ),
                Fields = [
                    FormField
                        .Text( TitleFieldId, "Title", true, 120 )
                        .WithDefault( draft.Title ),
                    FormField
                        .Multiline( BodyFieldId, "Draft", 8192 )
                        .WithDefault( draft.Body ),
                    FormField.Choice( CategoryFieldId, "Category", [new( NoteCategory, "Note" ), new( TaskCategory, "Task" )] ) with { DefaultValue = JsonSerializer.SerializeToElement( draft.Category, DraftJsonContext.Default.String ) },
                    FormField
                        .Checkbox( DoneFieldId, "Completed" )
                        .WithDefault( draft.Done )
                ]
            };
        }
    }

    sealed record Draft( string Title, string Body, string Category, bool Done );
    [JsonSourceGenerationOptions( PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase )]
    [JsonSerializable( typeof( Draft ) )]
    [JsonSerializable( typeof( string ) )]
    sealed partial class DraftJsonContext : JsonSerializerContext;
}

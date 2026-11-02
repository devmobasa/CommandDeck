using CommandDeck.Extensions.Views;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void AuditExtensionItemIds( )
    {
        foreach ( var id in new[] { new string( 'x', 128 ), new string( 'x', 129 ), new string( 'x', 256 ), new string( 'λ', 128 ) } )
        {
            ViewValidation.Validate( View( id ) );
            ProtocolRequestValidator.Validate( Request( id ) );
        }

        foreach ( var id in new[] { new string( 'x', 257 ), new string( 'λ', 129 ) } )
        {
            Throws<InvalidDataException>( ( ) => ViewValidation.Validate( View( id ) ) );
            Throws<InvalidOperationException>( ( ) => ProtocolRequestValidator.Validate( Request( id ) ) );
        }

        static ListView View( string id ) => new( ) { Id = "list", Title = "Rows", Items = [new ListItem { Id = id, Title = "Row" }] };

        static ProtocolRequest Request( string id ) => new( )
        {
            RequestId = 1,
            Operation = "extension.action",
            SessionId = "session",
            ExtensionInput = new ExtensionSessionInput { ItemId = id }
        };
    }
}

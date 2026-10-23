using Tmds.DBus.Protocol;

namespace CommandDeck;

public sealed partial class PackageHelperClient
{
    private static MessageBuffer BuildStatusRequest( DBusConnection connection )
    {
        using var writer = connection.GetMessageWriter( );

        writer.WriteMethodCallHeader( PackageHelperProtocol.BusName, PackageHelperProtocol.ObjectPath, PackageHelperProtocol.InterfaceName, null,
            PackageHelperProtocol.MethodGetStatus, MessageFlags.None );

        return writer.CreateMessage( );
    }

    private static MessageBuffer BuildCommitRequest( DBusConnection connection, string operation, IReadOnlyList<string> names, string databaseIdentity,
        string transactionDigest )
    {
        using var writer = connection.GetMessageWriter( );

        writer.WriteMethodCallHeader( PackageHelperProtocol.BusName, PackageHelperProtocol.ObjectPath, PackageHelperProtocol.InterfaceName, "sasss",
            PackageHelperProtocol.MethodCommit, MessageFlags.AllowInteractiveAuthorization );

        writer.WriteString( operation );
        string[] requested = [.. names];
        writer.WriteArray( requested );
        writer.WriteString( databaseIdentity );
        writer.WriteString( transactionDigest );

        return writer.CreateMessage( );
    }

    private static MessageBuffer BuildOperationRequest( DBusConnection connection, string member, string operationId )
    {
        using var writer = connection.GetMessageWriter( );

        writer.WriteMethodCallHeader( PackageHelperProtocol.BusName, PackageHelperProtocol.ObjectPath, PackageHelperProtocol.InterfaceName, "s", member,
            MessageFlags.None );

        writer.WriteString( operationId );

        return writer.CreateMessage( );
    }

    private static HelperStatusReply ReadStatus( Message message, object? _ )
    {
        var reader = message.GetBodyReader( );

        return new HelperStatusReply( reader.ReadString( ), reader.ReadString( ), reader.ReadBool( ) );
    }

    private static OperationReply ReadOperation( Message message, object? _ )
    {
        var reader = message.GetBodyReader( );

        return new OperationReply( reader.ReadString( ), reader.ReadString( ), reader.ReadArrayOfString( ), reader.ReadArrayOfString( ) );
    }
}

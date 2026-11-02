using CommandDeck.PackageHelper;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AuditPackageRetentionAsync( )
    {
        using var fixture = PackageHelperFixture.Create( );
        fixture.Pacman.PrintOutput = "alpha|1.0|core\n";
        fixture.Pacman.QueryOutput = "alpha 1.0\n";
        List<string> ids = [];
        for ( var index = 0; index < 66; index++ )
        {
            var id = await fixture.CommitAsync( "install", ["alpha"] );
            ids.Add( id );
            if ( index % 2 == 0 )
            {
                await fixture.Core.RunQueuedOperationAsync( default );
                Equal( "completed", fixture.Core.GetOperation( id ).State );
            }
            else
            {
                True( fixture.Core.Cancel( id ) );
                await fixture.Core.RunQueuedOperationAsync( default );
                Equal( "cancelled", fixture.Core.GetOperation( id ).State );
            }
        }

        var active = await fixture.CommitAsync( "install", ["alpha"] );
        Equal( "queued", fixture.Core.GetOperation( active ).State );
        foreach ( var id in ids.Take( 2 ) )
        {
            Throws<PackageHelperException>( ( ) => fixture.Core.GetOperation( id ) );
        }

        foreach ( var id in ids.Skip( 2 ) )
        {
            True( fixture.Core.GetOperation( id ).State is "completed" or "cancelled" );
        }

        await fixture.Core.RunQueuedOperationAsync( default );
        Equal( "completed", fixture.Core.GetOperation( active ).State );
        Throws<PackageHelperException>( ( ) => fixture.Core.GetOperation( ids[2] ) );
    }
}

using System.Runtime.InteropServices;

namespace CommandDeck;

internal static class LinuxDirectoryDurability
{
    public static void FlushParent( string path )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return;
        }

        var directory = Path.GetDirectoryName( path );
        if ( string.IsNullOrEmpty( directory ) )
        {
            return;
        }

        var descriptor = Open( directory, LinuxConstants.OReadOnly | LinuxConstants.ODirectory | LinuxConstants.OCloseOnExec );
        if ( descriptor < 0 )
        {
            throw new IOException( $"Could not open journal directory for durability: errno {Marshal.GetLastPInvokeError( )}." );
        }

        try
        {
            if ( Fsync( descriptor ) != 0 )
            {
                throw new IOException( $"Could not flush journal directory: errno {Marshal.GetLastPInvokeError( )}." );
            }
        }
        finally
        {
            _ = Close( descriptor );
        }
    }

    [DllImport( NativeLibraryNames.LibC, EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true,
        SetLastError = true )]
    private static extern int Open( string path, int flags );

    [DllImport( NativeLibraryNames.LibC, EntryPoint = "fsync", SetLastError = true )]
    private static extern int Fsync( int descriptor );

    [DllImport( NativeLibraryNames.LibC, EntryPoint = "close" )]
    private static extern int Close( int descriptor );
}

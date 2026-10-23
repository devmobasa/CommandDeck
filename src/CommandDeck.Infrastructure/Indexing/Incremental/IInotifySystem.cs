using System.Runtime.InteropServices;

namespace CommandDeck;

// The kernel calls the directory watcher makes. Each call returns a
// non-negative result or a negated errno. Specs wrap the Linux calls to
// simulate a watch limit without changing the user's inotify settings.
internal interface IInotifySystem
{
    int CreateInstance( );

    int AddWatch( int instance, string path, uint mask );

    void RemoveWatch( int instance, int watch );

    int CreateWake( );

    void Wake( int wake );

    // Blocks until a descriptor is readable; marks which ones are.
    int Poll( ReadOnlySpan<int> descriptors, Span<bool> readable );

    int Read( int descriptor, Span<byte> buffer );

    void Close( int descriptor );
}

internal sealed unsafe class LinuxInotifySystem : IInotifySystem
{
    private const int MaximumStackDescriptors = 256;

    public static LinuxInotifySystem Instance { get; } = new( );

    public int CreateInstance( ) =>
        Result( InotifyNative.Initialize( LinuxConstants.ONonBlocking | LinuxConstants.OCloseOnExec ) );

    public int AddWatch( int instance, string path, uint mask ) =>
        Result( InotifyNative.AddWatch( instance, path, mask ) );

    public void RemoveWatch( int instance, int watch ) =>
        _ = InotifyNative.RemoveWatch( instance, watch );

    public int CreateWake( ) =>
        Result( InotifyNative.CreateEvent( 0, LinuxConstants.ONonBlocking | LinuxConstants.OCloseOnExec ) );

    public void Wake( int wake )
    {
        ulong increment = 1;
        nint written;

        do
        {
            written = InotifyNative.Write( wake, ( byte* ) &increment, sizeof( ulong ) );
        } while ( written < 0 && Marshal.GetLastPInvokeError( ) == LinuxConstants.InterruptedError );
    }

    public int Poll( ReadOnlySpan<int> descriptors, Span<bool> readable )
    {
        if ( readable.Length < descriptors.Length )
        {
            throw new ArgumentOutOfRangeException( nameof( descriptors ) );
        }

        var count = descriptors.Length;

        Span<InotifyNative.PollEntry> entries = count <= MaximumStackDescriptors
            ? stackalloc InotifyNative.PollEntry[count]
            : new InotifyNative.PollEntry[count];

        for ( var index = 0; index < descriptors.Length; index++ )
        {
            entries[index] = new InotifyNative.PollEntry { Descriptor = descriptors[index], Events = InotifyNative.PollIn };
        }

        int result;

        do
        {
            fixed ( InotifyNative.PollEntry* pointer = entries )
            {
                result = InotifyNative.Poll( pointer, ( nuint ) descriptors.Length, -1 );
            }
        } while ( result < 0 && Marshal.GetLastPInvokeError( ) == LinuxConstants.InterruptedError );

        if ( result < 0 )
        {
            return -Marshal.GetLastPInvokeError( );
        }

        for ( var index = 0; index < descriptors.Length; index++ )
        {
            readable[index] = entries[index].ReturnedEvents != 0;
        }

        return result;
    }

    public int Read( int descriptor, Span<byte> buffer )
    {
        fixed ( byte* pointer = buffer )
        {
            var count = InotifyNative.Read( descriptor, pointer, ( nuint ) buffer.Length );

            return count < 0 ? -Marshal.GetLastPInvokeError( ) : ( int ) count;
        }
    }

    public void Close( int descriptor ) => _ = InotifyNative.Close( descriptor );

    private static int Result( int value ) => value >= 0 ? value : -Marshal.GetLastPInvokeError( );
}

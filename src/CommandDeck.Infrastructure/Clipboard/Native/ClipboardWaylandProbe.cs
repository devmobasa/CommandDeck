using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace CommandDeck;

public sealed record ClipboardWaylandCapabilities( bool RegistryReady, uint ExtDataControlVersion, int SeatCount );

/// <summary>Registry-only child probe. Never creates a data device or observes a selection.</summary>
public sealed unsafe class ClipboardWaylandProbe : IDisposable
{
    private const int MaximumInterfaceNameBytes = 256;

    private nint _display;
    private nint _registry;
    private nint _callback;
    private GCHandle _self;
    private bool _ready;
    private bool _failed;
    private uint _extVersion;
    private int _seats;
    private int _globals;

    public ClipboardWaylandCapabilities Run( string? displayName, CancellationToken cancellationToken )
    {
        if ( _display != 0 || _self.IsAllocated )
        {
            throw new InvalidOperationException( "Clipboard registry probe cannot be reused." );
        }

        _self = GCHandle.Alloc( this );
        _display = WaylandNative.Connect( displayName );

        if ( _display == 0 )
        {
            throw new IOException( "Clipboard Wayland display is unavailable." );
        }

        WaylandArgument argument = default;
        _registry = WaylandNative.MarshalArray( _display, 1, ClipboardWaylandInterfaces.Get( ClipboardWaylandInterfaceNames.Registry ), 1, 0, &argument );

        Attach( _registry );

        _callback = WaylandNative.MarshalArray( _display, 0, ClipboardWaylandInterfaces.Get( ClipboardWaylandInterfaceNames.Callback ), 1, 0, &argument );

        Attach( _callback );
        var started = Stopwatch.GetTimestamp( );

        while ( !_ready && !_failed )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            if ( Stopwatch.GetElapsedTime( started ) >= TimeSpan.FromSeconds( 2 ) )
            {
                throw new TimeoutException( "Clipboard registry probe timed out." );
            }

            WaylandDisplayPump.Dispatch( _display );
        }

        if ( _failed )
        {
            throw new IOException( "Clipboard registry discovery failed or changed." );
        }

        return new( _ready, _extVersion, _seats );
    }

    private void Attach( nint proxy )
    {
        if ( proxy == 0 || WaylandNative.AddDispatcher( proxy, &Dispatch, GCHandle.ToIntPtr( _self ), 0 ) != 0 )
        {
            throw new IOException( "Clipboard Wayland dispatcher could not be installed." );
        }
    }

    [UnmanagedCallersOnly( CallConvs = [typeof( CallConvCdecl )] )]
    private static int Dispatch( nint implementation, nint target, uint opcode, nint message, WaylandArgument* arguments )
    {
        var probe = ( ClipboardWaylandProbe? ) GCHandle.FromIntPtr( implementation ).Target;

        if ( probe is null )
        {
            return -1;
        }

        try
        {
            if ( IsSyncEvent( target, opcode, probe ) )
            {
                probe._ready = true;
            }
            else if ( IsRegistryEvent( target, opcode, probe ) )
            {
                if ( ++probe._globals > ClipboardWaylandCapture.MaximumGlobals )
                {
                    probe._failed = true;

                    return -1;
                }

                var name = ReadInterfaceName( arguments[1].Pointer );
                if ( name == ClipboardWaylandInterfaceNames.DataControlManager )
                {
                    probe._extVersion = arguments[2].Uint;
                }

                if ( name == ClipboardWaylandInterfaceNames.Seat )
                {
                    probe._seats++;
                }
            }
            else
            {
                probe._failed = true;

                return -1;
            }

            return 0;
        }
        catch
        {
            probe._failed = true;

            return -1;
        }
    }

    private static string ReadInterfaceName( nint pointer )
    {
        if ( pointer == 0 )
        {
            throw new InvalidDataException( "Wayland interface name is missing." );
        }

        var bytes = ( byte* ) pointer;
        var length = 0;
        while ( length < MaximumInterfaceNameBytes && bytes[length] != 0 )
        {
            length++;
        }

        if ( length == MaximumInterfaceNameBytes )
        {
            throw new InvalidDataException( "Wayland interface name is oversized." );
        }

        return Encoding.ASCII.GetString( new ReadOnlySpan<byte>( bytes, length ) );
    }

    public void Dispose( )
    {
        if ( _callback != 0 )
        {
            WaylandNative.DestroyProxy( _callback );
        }

        if ( _registry != 0 )
        {
            WaylandNative.DestroyProxy( _registry );
        }

        if ( _display != 0 )
        {
            WaylandNative.Disconnect( _display );
        }

        _callback = _registry = _display = 0;

        if ( _self.IsAllocated )
        {
            _self.Free( );
        }
    }

    private static bool IsSyncEvent( nint target, uint opcode, ClipboardWaylandProbe probe )
    {
        return target == probe._callback && opcode == 0;
    }

    private static bool IsRegistryEvent( nint target, uint opcode, ClipboardWaylandProbe probe )
    {
        return target == probe._registry && opcode == 0;
    }
}

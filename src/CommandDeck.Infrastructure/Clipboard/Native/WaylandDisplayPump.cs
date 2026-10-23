using System.Runtime.InteropServices;

namespace CommandDeck;

internal static unsafe class WaylandDisplayPump
{
    private const int DefaultPollTimeoutMilliseconds = 20;

    // A negative timeout waits until the display, the receive descriptor or the wake signal is ready.
    internal static void Dispatch( nint display, int receiveDescriptor = -1, int timeoutMilliseconds = DefaultPollTimeoutMilliseconds,
        WaylandWakeSignal? wake = null )
    {
        if ( WaylandNative.PrepareRead( display ) != 0 )
        {
            DispatchPending( display );

            return;
        }

        var prepared = true;

        try
        {
            var flush = WaylandNative.Flush( display );
            if ( HasFlushFailed( flush ) )
            {
                throw new IOException( "Clipboard Wayland request flush failed." );
            }

            PollDescriptor* descriptors = stackalloc PollDescriptor[3];

            descriptors[0] = new( ) { Fd = WaylandNative.GetFd( display ), Events = ( short ) (1 | (flush < 0 ? 4 : 0)) };
            descriptors[1] = new( ) { Fd = receiveDescriptor, Events = 1 };
            descriptors[2] = new( ) { Fd = wake?.Descriptor ?? -1, Events = 1 };
            var polled = WaylandNative.Poll( descriptors, 3, timeoutMilliseconds );
            var descriptor = descriptors[0];
            if ( HasPollFailed( polled, descriptor ) )
            {
                throw new IOException( "Clipboard Wayland connection closed." );
            }

            // Drained before the caller's next pass reads its queues, so a later signal is never lost.
            if ( IsWakeReady( descriptors, polled ) )
            {
                wake!.Drain( );
            }

            if ( IsDisplayReady( polled, descriptor ) )
            {
                prepared = false;

                if ( WaylandNative.ReadEvents( display ) < 0 )
                {
                    throw new IOException( "Clipboard Wayland event read failed." );
                }
            }
        }
        finally
        {
            if ( prepared )
            {
                WaylandNative.CancelRead( display );
            }
        }

        DispatchPending( display );
    }

    private static void DispatchPending( nint display )
    {
        if ( WaylandNative.DispatchPending( display ) < 0 )
        {
            throw new IOException( "Clipboard Wayland event dispatch failed." );
        }
    }

    private static bool HasFlushFailed( int flush )
    {
        return flush < 0 && Marshal.GetLastPInvokeError( ) != LinuxConstants.TryAgain;
    }

    private static bool HasPollFailed( int polled, PollDescriptor descriptor )
    {
        return polled < 0 && Marshal.GetLastPInvokeError( ) != LinuxConstants.InterruptedError || (descriptor.ReturnedEvents & (8 | 16 | 32)) != 0;
    }

    private static bool IsWakeReady( PollDescriptor* descriptors, int polled )
    {
        return polled > 0 && (descriptors[2].ReturnedEvents & 1) != 0;
    }

    private static bool IsDisplayReady( int polled, PollDescriptor descriptor )
    {
        return polled > 0 && (descriptor.ReturnedEvents & 1) != 0;
    }
}

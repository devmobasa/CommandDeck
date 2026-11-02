namespace CommandDeck;

// The lease belongs to the socket, not a QML object. Every transition is
// serialized with the watchdog so a late start cannot outlive its stop.
public sealed class ShortcutRecordingSession : IAsyncDisposable
{
    private const int RecordingLeaseSeconds = 10;

    private readonly ExternalProcessRunner _run;
    private readonly Func<bool> _available;
    private readonly Func<bool> _sessionAvailable;
    private readonly SemaphoreSlim _gate = new( 1, 1 );
    private readonly ITimer _watchdog;
    private string? _owner;

    public ShortcutRecordingSession( ExternalProcessRunner run, Func<bool> available, TimeProvider? clock = null, Func<bool>? sessionAvailable = null )
    {
        _run = run;
        _available = available;
        _sessionAvailable = sessionAvailable ?? available;
        _watchdog = (clock ?? TimeProvider.System).CreateTimer( _ => _ = StopAfterTimeoutAsync( ), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan );
    }

    public async ValueTask<bool> StartAsync( string owner, CancellationToken cancellationToken )
    {
        await _gate.WaitAsync( cancellationToken ).ConfigureAwait( false );
        try
        {
            if ( !_available( ) )
            {
                return false;
            }

            if ( _owner is not null && _owner != owner )
            {
                throw new InvalidOperationException( "Another window is recording a shortcut." );
            }
            // Arm before dispatch; an ambiguous dispatch failure still gets reset.
            _owner = owner;
            _watchdog.Change( TimeSpan.FromSeconds( RecordingLeaseSeconds ), Timeout.InfiniteTimeSpan );
            try
            {
                await DispatchAsync( ShortcutLua.RecordingSubmap, cancellationToken ).ConfigureAwait( false );
                var current = await _run( ToolExecutables.Hyprctl, ["submap"], HyprlandDispatch.Timeout, cancellationToken ).ConfigureAwait( false );
                if ( current is not { ExitCode: 0, TimedOut: false, AnyOutputLimitExceeded: false } || current.StandardOutput.Trim( ) !=
                     ShortcutLua.RecordingSubmap )
                {
                    throw new InvalidOperationException( "Hyprland did not enter the recording submap." );
                }
            }
            catch
            {
                await ResetAsync( ).ConfigureAwait( false );

                throw;
            }
            // Preparation can consume part of the provisional safety lease.
            // Give the confirmed capture its full window before acknowledging it.
            _watchdog.Change( TimeSpan.FromSeconds( RecordingLeaseSeconds ), Timeout.InfiniteTimeSpan );

            return true;
        }
        finally
        {
            _gate.Release( );
        }
    }

    public async ValueTask StopAsync( string? owner = null )
    {
        await _gate.WaitAsync( ).ConfigureAwait( false );
        try
        {
            if ( owner is not null && _owner != owner )
            {
                return;
            }

            await ResetAsync( ).ConfigureAwait( false );
        }
        finally
        {
            _gate.Release( );
        }
    }

    public async ValueTask ResetOnStartupAsync( CancellationToken cancellationToken )
    {
        if ( !_sessionAvailable( ) )
        {
            return;
        }

        var result = await _run( ToolExecutables.Hyprctl, ["submap"], HyprlandDispatch.Timeout, cancellationToken ).ConfigureAwait( false );
        if ( result.ExitCode == 0 && result.StandardOutput.Trim( ) == ShortcutLua.RecordingSubmap )
        {
            await DispatchAsync( "reset", cancellationToken ).ConfigureAwait( false );
        }
    }

    private async ValueTask ResetAsync( )
    {
        if ( _owner is null )
        {
            return;
        }
        // Keep the owner and retry timer on failure. Never report a successful stop
        // when the compositor did not acknowledge it.
        try
        {
            var current = await _run( ToolExecutables.Hyprctl, ["submap"], HyprlandDispatch.Timeout, CancellationToken.None ).ConfigureAwait( false );
            if ( current is not { ExitCode: 0, TimedOut: false, AnyOutputLimitExceeded: false } )
            {
                throw new InvalidOperationException( "Could not check the recording submap. Press Escape to leave it." );
            }

            if ( current.StandardOutput.Trim( ) == ShortcutLua.RecordingSubmap )
            {
                await DispatchAsync( "reset", CancellationToken.None ).ConfigureAwait( false );
            }

            _owner = null;
            _watchdog.Change( Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan );
        }
        catch
        {
            _watchdog.Change( TimeSpan.FromSeconds( 1 ), Timeout.InfiniteTimeSpan );

            throw;
        }
    }

    private async Task StopAfterTimeoutAsync( )
    {
        try
        {
            await StopAsync( ).ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            /* watchdog retries */
        }
    }

    private async ValueTask DispatchAsync( string submap, CancellationToken cancellationToken )
    {
        var result = await _run( ToolExecutables.Hyprctl, [HyprlandDispatch.Command, "submap", submap], HyprlandDispatch.Timeout,
             cancellationToken ).ConfigureAwait( false );
        if ( !HyprlandDispatch.WasAccepted( result ) )
        {
            throw new InvalidOperationException( "Hyprland could not change the recording submap. Press Escape to leave it." );
        }
    }

    public async ValueTask DisposeAsync( )
    {
        await StopAsync( ).ConfigureAwait( false );
        await _watchdog.DisposeAsync( ).ConfigureAwait( false );
        _gate.Dispose( );
    }
}

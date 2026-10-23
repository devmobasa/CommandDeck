using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

public sealed partial class HerdrClient
{
    private int _focusInProgress;

    public async ValueTask<HerdrFocusOutcome> FocusAsync( string targetKind, string targetId, CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<ActionExecutionResult>>? activateWindow = null )
    {
        cancellationToken.ThrowIfCancellationRequested( );

        if ( !TryBeginFocus( ) )
        {
            return FocusBusy( );
        }

        try
        {
            return await FocusAndActivateAsync( targetKind, targetId, activateWindow, cancellationToken )
                .ConfigureAwait( false );
        }
        finally
        {
            EndFocus( );
        }
    }

    // Both public action paths hold this client's gate before entering here.
    // The activation callback includes temporary-title cleanup on every exit.
    private async ValueTask<HerdrFocusOutcome> FocusAndActivateAsync( string targetKind, string targetId,
        Func<CancellationToken, ValueTask<ActionExecutionResult>>? activateWindow, CancellationToken cancellationToken )
    {
        var focused = await DispatchFocusAsync( targetKind, targetId, cancellationToken )
            .ConfigureAwait( false );

        if ( CannotActivateWindow( activateWindow, focused ) )
        {
            return focused;
        }

        var activation = await activateWindow( cancellationToken )
            .ConfigureAwait( false );

        return GetActivationOutcome( focused, activation );
    }

    private bool TryBeginFocus( ) => Interlocked.CompareExchange( ref _focusInProgress, 1, 0 ) == 0;

    private void EndFocus( ) => Interlocked.Exchange( ref _focusInProgress, 0 );

    private static HerdrFocusOutcome FocusBusy( ) => Failure( "Another Herdr focus action is already in progress." );

    private static bool CannotActivateWindow( [NotNullWhen( false )] Func<CancellationToken, ValueTask<ActionExecutionResult>>? activateWindow,
        HerdrFocusOutcome focused )
    {
        return !focused.Succeeded || activateWindow is null;
    }

    private static HerdrFocusOutcome GetActivationOutcome( HerdrFocusOutcome focused, ActionExecutionResult activation )
    {
        if ( activation.Succeeded )
        {
            return focused;
        }

        return Failure( activation.Message ?? "Herdr switched, but could not focus its window.", activation.DetailMarkdown );
    }
}

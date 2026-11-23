using System.Diagnostics;

namespace CommandDeck;

// Herdr runs as a terminal app (herdr, its default session) and as a desktop
// app (herdr-app, its open window), and each keeps its own spaces. Search
// reads both at once, or only the one a browsed space belongs to, and lists
// whatever has answered: one that is missing, stopped, closed, failing, or
// still busy near the search deadline hides none of the other's rows.
public sealed partial class HerdrProvider
{
    // The coordinator keeps only what a provider returns before its deadline,
    // so the lists get until a reserve before it for building the rows.
    private static readonly long MaximumDeadlineReserve = Stopwatch.Frequency / 20;

    private bool IsInstalled => client.IsInstalled || app?.IsInstalled == true;

    // The coordinator's deadline less the reserve, at most a quarter of the
    // time left; null when no coordinator set a deadline.
    private static long? Cutoff( SearchRequest request, long started )
    {
        var deadline = request.ProviderDeadlineTimestamp;
        if ( deadline <= 0 )
        {
            return null;
        }

        return deadline - Math.Min( MaximumDeadlineReserve, Math.Max( deadline - started, 0 ) / 4 );
    }

    private async ValueTask<HerdrInventory?> ListAsync( SearchRequest request, Func<HerdrInventory, bool> showsRows, CancellationToken cancellationToken )
    {
        var cutoff = Cutoff( request, Stopwatch.GetTimestamp( ) );
        (var useTerminal, var useDesktop) = Backends( request.HerdrWorkspaceId );
        var lists = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        var terminal = useTerminal ? ListTerminalAsync( lists.Token ) : Task.FromResult<HerdrInventory?>( null );
        var desktop = useDesktop ? ListAppAsync( lists.Token ) : Task.FromResult<HerdrAppInventory?>( null );

        await WaitForAnswersAsync( [terminal, desktop], ( ) => Merge( Answer( terminal ), Answer( desktop )?.Inventory ) is { } partial && showsRows( partial ),
                cutoff, lists, cancellationToken )
            .ConfigureAwait( false );

        return Merge( Answer( terminal ), Answer( desktop )?.Inventory );
    }

    private async ValueTask<(HerdrInventory? Inventory, IReadOnlyList<HerdrAgent>? Agents)> ListWithAgentsAsync( SearchRequest request,
        Func<HerdrInventory, IReadOnlyList<HerdrAgent>, bool> showsRows, CancellationToken cancellationToken )
    {
        var cutoff = Cutoff( request, Stopwatch.GetTimestamp( ) );
        (var useTerminal, var useDesktop) = Backends( request.HerdrWorkspaceId );
        var lists = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        var terminal = useTerminal ? ListTerminalAsync( lists.Token ) : Task.FromResult<HerdrInventory?>( null );
        var desktop = useDesktop ? ListAppAsync( lists.Token ) : Task.FromResult<HerdrAppInventory?>( null );
        var terminalAgents = useTerminal && client.IsInstalled
            ? client
                .ListAgentsAsync( lists.Token )
                .AsTask( )
            : Task.FromResult<IReadOnlyList<HerdrAgent>?>( null );

        await WaitForAnswersAsync( [terminal, desktop, terminalAgents], ( ) => Collect( ) is ({ } partial, { } listed ) && showsRows( partial, listed ),
                cutoff, lists, cancellationToken )
            .ConfigureAwait( false );

        return Collect( );

        // The terminal app's agents are named through its own inventory, so
        // they count only when both of its lists answered.
        (HerdrInventory? Inventory, IReadOnlyList<HerdrAgent>? Agents) Collect( )
        {
            var terminalInventory = Answer( terminal );
            var terminalAgentList = Answer( terminalAgents );
            var terminalAnswered = terminalInventory is not null && terminalAgentList is not null;
            var listing = Answer( desktop );
            var inventory = Merge( terminalAnswered ? terminalInventory : null, listing?.Inventory );
            if ( inventory is null )
            {
                return (null, null);
            }

            IReadOnlyList<HerdrAgent> agents =
            [
                .. terminalAnswered ? terminalAgentList!.Where( static agent => !IsReserved( agent ) ) : [],
                .. listing?.Agents ?? []
            ];

            return (inventory, agents);
        }
    }

    // A browsed space needs only the app it belongs to.
    private static (bool Terminal, bool Desktop) Backends( string? scope ) =>
        HerdrScopes.TryParse( scope, out var desktop, out _ ) ? (!desktop, desktop) : (true, true);

    // Waits for every list until the cutoff; after it, returns as soon as what
    // has answered shows rows, since returning early pays off only then. Lists
    // still running are then cancelled, and their failures are observed.
    private static async Task WaitForAnswersAsync( Task[] lists, Func<bool> answered, long? cutoffAt, CancellationTokenSource running,
        CancellationToken cancellationToken )
    {
        var all = Task.WhenAll( lists );
        Observe( all );

        using var timer = CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );
        var stopped = Task.Delay( Timeout.InfiniteTimeSpan, timer.Token );
        var cutoff = cutoffAt is { } at ? Task.Delay( Max( Stopwatch.GetElapsedTime( Stopwatch.GetTimestamp( ), at ), TimeSpan.Zero ), timer.Token ) : null;

        while ( true )
        {
            // The wake set is taken before the checks, so anything that ends
            // after them still wakes the wait.
            List<Task> wakes = [all, stopped, .. lists.Where( static list => !list.IsCompleted )];
            if ( cutoff is { IsCompleted: false } )
            {
                wakes.Add( cutoff );
            }

            if ( all.IsCompleted || cancellationToken.IsCancellationRequested || cutoff is { IsCompleted: true } && answered( ) )
            {
                break;
            }

            await Task
                .WhenAny( wakes )
                .ConfigureAwait( false );
        }

        await timer
            .CancelAsync( )
            .ConfigureAwait( false );

        // Late lists are stopped without waiting for their cancellation
        // callbacks, which could outlast the reserve; the source is disposed
        // once those callbacks and every list have finished.
        var settled = Task.WhenAll( running.CancelAsync( ), all );
        Observe( settled );
        _ = settled.ContinueWith( _ => running.Dispose( ), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default );

        foreach ( var list in lists.Where( static list => !list.IsCompletedSuccessfully ) )
        {
            Observe( list );
        }

        cancellationToken.ThrowIfCancellationRequested( );
    }

    private static TimeSpan Max( TimeSpan left, TimeSpan right ) => left > right ? left : right;

    private static void Observe( Task task ) =>
        _ = task.ContinueWith( static completed => _ = completed.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default );

    private static T? Answer<T>( Task<T?> list ) where T : class => list.IsCompletedSuccessfully ? list.Result : null;

    private Task<HerdrInventory?> ListTerminalAsync( CancellationToken cancellationToken ) =>
        client.IsInstalled
            ? client
                .ListAsync( cancellationToken )
                .AsTask( )
            : Task.FromResult<HerdrInventory?>( null );

    private Task<HerdrAppInventory?> ListAppAsync( CancellationToken cancellationToken ) =>
        app is { IsInstalled: true }
            ? app
                .ListAsync( cancellationToken )
                .AsTask( )
            : Task.FromResult<HerdrAppInventory?>( null );

    // A terminal-app ID that looks like a desktop scope is left out, so every
    // scope, join, and row ID names exactly one app.
    private static HerdrInventory? Merge( HerdrInventory? terminal, HerdrInventory? desktop )
    {
        var listed = terminal is null
            ? null
            : new HerdrInventory
            {
                Workspaces = [.. terminal.Workspaces.Where( static space => !HerdrScopes.IsReserved( space.Id ) )],
                Tabs = [.. terminal.Tabs.Where( static tab => !HerdrScopes.IsReserved( tab.WorkspaceId ) )]
            };

        if ( listed is null || desktop is null )
        {
            return listed ?? desktop;
        }

        return new HerdrInventory { Workspaces = [.. listed.Workspaces, .. desktop.Workspaces], Tabs = [.. listed.Tabs, .. desktop.Tabs] };
    }

    private static bool IsReserved( HerdrAgent agent ) => HerdrScopes.IsReserved( agent.WorkspaceId ) || HerdrScopes.IsReserved( agent.PaneId );
}

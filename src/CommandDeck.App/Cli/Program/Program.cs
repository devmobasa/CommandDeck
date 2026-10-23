using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const string PauseCommand = "pause";
    private const string ResumeCommand = "resume";
    private const string UnknownValue = "unknown";

    private const int MaximumPackageSearchResults = 16;
    private const string NoneValue = "none";
    private const int FollowUpRequestId = 2;
    private const int InitialRequestId = 1;
    private const string EnabledValue = "enabled";
    private const string DisabledValue = "disabled";
    private const int MaximumSearchResults = 200;
    private const string ListCommand = "list";
    private const string RemoveCommand = "remove";
    private const string ResetCommand = "reset";
    private const string ExportCommand = "export";
    private const int DefaultSearchResults = 20;
    private const string PreferencesCommand = "preferences";
    private const string IndexCommand = "index";
    private const string AgentCommand = "agent";
    private const string AiCommand = "ai";
    private const int InterruptedExitCode = 130;
    private const string QuicklinksCommand = "quicklinks";
    private const string DaemonCommand = "daemon";
    private const string ServeCommand = "serve";
    private const string ReindexCommand = "reindex";
    private const string StatusCommand = "status";
    private const string InitCommand = "init";

    private const string CatalogCommand = "catalog";

    public static async Task<int> Main( string[] args )
    {
        CliArguments arguments = new( args );
        AppLog.Verbose = arguments.HasFlag( CliOptionNames.Verbose );
        using CancellationTokenSource shutdown = new( );
        var terminateRequested = 0;

        // The registration must stay alive until Main returns: once it is collected, SIGTERM falls
        // back to the default handler and kills the daemon (exit 143) instead of shutting it down.
        using var terminateSignal = RegisterTerminateSignal( shutdown, ( ) => Interlocked.Exchange( ref terminateRequested, 1 ) );

        Console.CancelKeyPress += ( _, eventArgs ) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel( );
        };

        try
        {
            return arguments.Command.ToLowerInvariant( ) switch
            {
                CliArguments.HelpCommand or CliOptionNames.Help or "-h" => PrintHelp( ),
                "version" or CliOptionNames.Version => PrintVersion( ),
                "extensions" => await RunExtensionsCliAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false ),
                "extension-probe" => await RunExtensionProbeAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false ),
                DaemonChildProcessContract.ExtensionWorker => await ExtensionSandbox
                    .RunWorkerAsync( arguments.GetOption( DaemonChildProcessContract.Artifact ) ?? "",
                        arguments.GetOption( DaemonChildProcessContract.Entrypoint ) ?? "",
                        int.Parse( arguments.GetOption( DaemonChildProcessContract.OwnerPid ) ?? "0", System.Globalization.CultureInfo.InvariantCulture ),
                        long.Parse( arguments.GetOption( DaemonChildProcessContract.OwnerStart ) ?? "0", System.Globalization.CultureInfo.InvariantCulture ),
                        arguments.GetOption( DaemonChildProcessContract.DebugSocket ), shutdown.Token )
                    .ConfigureAwait( false ),
                "extension-debug-connect" => await ExtensionDebugTransport
                    .ConnectAsync( arguments.GetOption( CliOptionNames.Socket ) ?? "", shutdown.Token )
                    .ConfigureAwait( false ),
                "clipboard-frame" => await ClipboardCapture
                    .WriteFrameAsync( Console.OpenStandardInput( ), Console.Out, Environment.GetEnvironmentVariable( "CLIPBOARD_STATE" ), shutdown.Token )
                    .ConfigureAwait( false ),
                DaemonChildProcessContract.ClipboardHost => RunClipboardHost( arguments, shutdown.Token ),
                ClipboardChildCommands.ClipboardHtmlProject => RunClipboardHtmlProjection( ),
                ClipboardChildCommands.ClipboardActionWorker => ClipboardActionWorker.RunChild( ),
                "pick" => await RunPickerAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false ),
                "config" => await RunConfigAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false ),
                "hotkey" => RunHotkey( arguments ),
                "bridge" => await StdioBridge
                    .RunAsync( arguments.GetOption( CliOptionNames.Socket ), shutdown.Token )
                    .ConfigureAwait( false ),
                PreferencesCommand when IsPreferencesResetCommand( arguments ) => await RunPreferencesResetCliAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false ),
                IndexCommand when IsIndexRootsCommand( arguments ) => await RunIndexRootsCliAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false ),
                AgentCommand when AgentActionUsesDaemon( arguments ) => await RunAgentDaemonAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false ),
                AiCommand when AiActionUsesDaemon( arguments ) => await RunAiDaemonAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false ),
                _ => await RunWithRuntimeAsync( arguments, shutdown.Token )
                    .ConfigureAwait( false )
            };
        }
        catch ( OperationCanceledException ) when ( shutdown.IsCancellationRequested )
        {
            return Volatile.Read( ref terminateRequested ) == 1 ? 0 : InterruptedExitCode;
        }
        catch ( Exception exception )
        {
            AppLog.Error( exception.Message, AppLog.Verbose ? exception : null );

            return 1;
        }
    }

    private static async Task<int> RunWithRuntimeAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        var configPath = arguments.GetOption( CliOptionNames.Config );
        var command = arguments.Command.ToLowerInvariant( );

        await DisplaySizingMigration
            .InitializeAsync( configPath, cancellationToken )
            .ConfigureAwait( false );

        await using var runtime = await CommandDeckRuntime
            .CreateAsync( configPath, new RuntimeConstructionOptions { StartIndexer = command is not (CatalogCommand or QuicklinksCommand) },
                includeClipboardHistory: command is DaemonCommand or ServeCommand, cancellationToken )
            .ConfigureAwait( false );

        using var shutdown = cancellationToken.Register( runtime.CancelBackgroundIndexing );

        if ( await RequiresCatalogReindexAsync( command, runtime, cancellationToken )
                .ConfigureAwait( false ) )
        {
            AppLog.Info( "Catalog schema changed; rebuilding the index before continuing." );

            await runtime
                .Indexer.ReindexAsync( progress: null, cancellationToken )
                .ConfigureAwait( false );
        }

        return command switch
        {
            DaemonCommand or ServeCommand => await RunDaemonAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            "search" => await RunSearchAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            "suggest" => await RunSuggestAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            ReindexCommand => await RunReindexAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            IndexCommand => await RunIndexControlAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            AgentCommand => await RunAgentAsync( arguments, runtime, cancellationToken )
                .ConfigureAwait( false ),
            "doctor" => await Doctor
                .RunAsync( runtime, configPath, cancellationToken )
                .ConfigureAwait( false ),
            StatusCommand => await RunStatusAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            CatalogCommand => await RunCatalogAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            AiCommand => await RunAiAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            PreferencesCommand => await RunPreferencesAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            "ranking" => await RunRankingAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            "history" => await RunQueryHistoryAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            QuicklinksCommand => await RunQuicklinksAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            "filters" => await RunFilterAliasesAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            "saved-search" => await RunSavedSearchAsync( runtime, arguments, cancellationToken )
                .ConfigureAwait( false ),
            _ => UnknownCommand( arguments.Command )
        };
    }

    private static async Task<int> RunDaemonAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var executable = Environment.ProcessPath;
        if ( executable is null )
        {
            throw new InvalidOperationException( "Cannot locate CommandDeck executable." );
        }

        runtime.StartClipboardCapture( executable, GetProcessPrefix( executable ) );
        UnixProtocolServer server = new( runtime, arguments.GetOption( CliOptionNames.Socket ) );

        await server
            .RunAsync( cancellationToken )
            .ConfigureAwait( false );

        return 0;
    }

    private static async Task<int> RunSearchAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var query = arguments.GetOption( CliOptionNames.Query ) ?? arguments.JoinPositionalsAfterCommand( );

        var limit = ParseInteger( arguments.GetOption( CliOptionNames.Limit ), DefaultSearchResults, 1, MaximumSearchResults );

        var results = await runtime
            .SearchAsync( new SearchRequest { Query = query, Limit = limit }, cancellationToken )
            .ConfigureAwait( false );

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            var json = JsonSerializer.Serialize( results.ToList( ), CommandDeckJsonContext.Default.ListSearchResult );

            Console.WriteLine( json );

            return 0;
        }

        if ( results.Count == 0 )
        {
            Console.WriteLine( "No results." );

            return 0;
        }

        // Titles and subtitles come from file names, windows and other sources a
        // hostile author controls, so terminal controls are printed escaped.
        var index = 1;
        foreach ( var result in results )
        {
            Console.WriteLine( $"{index,2}. [{result.Kind}] {PreferencesTextFormatter.EscapeTerminalText( result.Title )}" );

            if ( !string.IsNullOrWhiteSpace( result.Subtitle ) )
            {
                Console.WriteLine( $"    {PreferencesTextFormatter.EscapeTerminalText( result.Subtitle )}" );
            }

            index++;
        }

        return 0;
    }

    private static async Task<int> RunStatusAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var status = await runtime
            .GetRuntimeStatusAsync( cancellationToken )
            .ConfigureAwait( false );

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( status, CommandDeckJsonContext.Default.RuntimeStatusSnapshot ) );

            return 0;
        }

        var stats = status.Catalog;
        Console.WriteLine( $"Items:        {stats.ItemCount}" );
        Console.WriteLine( $"Files:        {stats.FileCount}" );
        Console.WriteLine( $"Config keys:  {stats.ConfigCount}" );
        Console.WriteLine( $"Applications: {stats.ApplicationCount}" );
        Console.WriteLine( $"Invocations:  {stats.UsageEventCount}" );
        Console.WriteLine( $"Database:     {stats.DatabaseBytes} bytes" );
        Console.WriteLine( $"Watch roots:  {status.Indexing.WatchedRootCount}" );
        Console.WriteLine( $"Pending:      {status.Indexing.PendingEventCount}" );
        Console.WriteLine( $"Reconcile:    {status.Indexing.ReconciliationRequired}" );

        foreach ( var unwatchedRoot in status.Indexing.UnwatchedRoots )
        {
            Console.WriteLine( $"Unwatched:    {unwatchedRoot}" );
        }

        foreach ( var root in status.Indexing.Roots )
        {
            Console.WriteLine( $"Root:         {root.Path} watched={root.Watched} paused={root.Paused} " +
                               $"pending={root.PendingEventCount} indexed={root.IndexedItemCount}" );

            if ( !string.IsNullOrWhiteSpace( root.Remediation ) )
            {
                Console.WriteLine( $"  Next: {root.Remediation}" );
            }
        }

        if ( !string.IsNullOrWhiteSpace( status.Indexing.LastError ) )
        {
            Console.WriteLine( $"Index error:  {status.Indexing.LastError}" );
        }

        foreach ( var provider in status.Providers )
        {
            Console.WriteLine( $"{provider.Label}: {provider.State}" );

            if ( !string.IsNullOrWhiteSpace( provider.Detail ) )
            {
                Console.WriteLine( $"  {provider.Detail}" );
            }

            if ( !string.IsNullOrWhiteSpace( provider.Remediation ) )
            {
                Console.WriteLine( $"  Next: {provider.Remediation}" );
            }
        }

        return 0;
    }

    private static async Task<int> RunConfigAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        var positionals = arguments.PositionalsAfterCommand( );
        var action = positionals.Count > 0 ? positionals[0] : InitCommand;
        if ( !string.Equals( action, InitCommand, StringComparison.OrdinalIgnoreCase ) )
        {
            Console.Error.WriteLine( "Only 'commanddeck config init' is currently supported." );

            return 2;
        }

        var path = await ConfigurationLoader
            .WriteDefaultAsync( arguments.GetOption( CliOptionNames.Config ), arguments.HasFlag( CliOptionNames.Force ), cancellationToken )
            .ConfigureAwait( false );

        Console.WriteLine( path );

        return 0;
    }

    private static async Task<int> RunPreferencesAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var positionals = arguments.PositionalsAfterCommand( );
        var action = ReadCliAction( positionals, 0, ShowCommand );
        switch ( action )
        {
            case ShowCommand:
                var snapshot = await runtime
                    .GetPreferencesAsync( includeShortcuts: false, cancellationToken )
                    .ConfigureAwait( false );

                if ( arguments.HasFlag( CliOptionNames.Json ) )
                {
                    Console.WriteLine( JsonSerializer.Serialize( snapshot, CommandDeckJsonContext.Default.PreferencesSnapshot ) );

                    return 0;
                }

                foreach ( var line in PreferencesTextFormatter.Format( snapshot ) )
                {
                    Console.WriteLine( line );
                }

                return 0;
            case ExportCommand:
                var export = await runtime
                    .ExportCustomizationsAsync( cancellationToken )
                    .ConfigureAwait( false );

                Console.WriteLine( ProtocolJson.SerializeBoundedCustomizationExport( export ) );

                return 0;
            case ResetCommand:
                if ( !arguments.HasFlag( CliOptionNames.Yes ) )
                {
                    Console.Error.WriteLine( "preferences reset requires --yes." );

                    return 2;
                }

                await runtime
                    .ResetCustomizationsAsync( cancellationToken )
                    .ConfigureAwait( false );

                await (( IndexCoordinator ) runtime.Indexer)
                    .WaitForBackgroundReindexAsync( cancellationToken )
                    .ConfigureAwait( false );

                Console.WriteLine( "Launcher preferences and customizations reset. Added folders removed; configuration folders re-enabled." );

                return 0;
            default:
                Console.Error.WriteLine( "Use 'preferences show', 'preferences export', or 'preferences reset --yes'." );

                return 2;
        }
    }

    // SIGTERM cancels the shutdown token instead of killing the process; null where Linux signals are unavailable.
    private static PosixSignalRegistration? RegisterTerminateSignal( CancellationTokenSource shutdown, Action terminateRequested )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return null;
        }

        return PosixSignalRegistration.Create( PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            terminateRequested( );
            shutdown.Cancel( );
        } );
    }

    private static int PrintVersion( )
    {
        var assembly = typeof( Program ).Assembly;

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>( )
                          ?.InformationalVersion ??
                      assembly
                          .GetName( )
                          .Version?.ToString( ) ??
                      UnknownValue;

        Console.WriteLine( $"commanddeck {version}" );
        Console.WriteLine( $".NET {Environment.Version}" );
        Console.WriteLine( System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "runtime: JIT" : "runtime: Native AOT" );

        return 0;
    }

    private static int UnknownCommand( string command )
    {
        Console.Error.WriteLine( $"Unknown command '{command}'. Run 'commanddeck help'." );

        return 2;
    }

    private static int ParseInteger( string? value, int fallback, int minimum, int maximum )
    {
        if ( int.TryParse( value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed ) )
        {
            return Math.Clamp( parsed, minimum, maximum );
        }
        else
        {
            return fallback;
        }
    }

    private static async ValueTask<bool> RequiresCatalogReindexAsync( string command, CommandDeckRuntime runtime, CancellationToken cancellationToken )
    {
        return command is not (DaemonCommand or ServeCommand or ReindexCommand or CatalogCommand or IndexCommand or QuicklinksCommand) &&
               await runtime
                   .Catalog.RequiresReindexAsync( cancellationToken )
                   .ConfigureAwait( false );
    }
}

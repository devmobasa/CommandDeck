using System.Text.Json;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions.Views;

namespace CommandDeck.Extensions;

public interface IExtensionCommand
{
    string Id { get; }

    string Mode => ExtensionCommandModes.View;

    ValueTask OpenAsync( CommandContext context, CancellationToken cancellationToken );
    ValueTask HandleAsync( CommandContext context, CommandEvent commandEvent, CancellationToken cancellationToken );
}

public static partial class ExtensionApp
{
    private static readonly JsonElement EmptyObject = JsonElement.Parse( "{}" );

    public static Task RunAsync( string[] args, params IExtensionCommand[] commands )
    {
        ArgumentNullException.ThrowIfNull( args );

        if ( args.Length != 0 )
        {
            throw new ArgumentException( "Extension commands receive input from their host, not command-line arguments.", nameof( args ) );
        }

        return RunAsync( Console.OpenStandardInput( ), Console.OpenStandardOutput( ), commands, CancellationToken.None );
    }

    /// <summary>Runs the same public protocol over supplied streams, including author conformance tests.</summary>
    public static Task RunAsync( Stream input, Stream output, IReadOnlyList<IExtensionCommand> commands, CancellationToken cancellationToken ) =>
        RunAsync( input, output, commands, Console.Error, cancellationToken );

    internal static async Task RunAsync( Stream input, Stream output, IReadOnlyList<IExtensionCommand> commands, TextWriter diagnostics,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( commands );

        if ( HasInvalidCommands( commands ) )
        {
            throw new ArgumentException( "Register 1-32 commands with supported modes.", nameof( commands ) );
        }

        foreach ( var command in commands )
        {
            ViewValidation.ValidateId( command.Id );
        }

        if ( commands
                 .Select( command => command.Id )
                 .Distinct( StringComparer.Ordinal )
                 .Count( ) !=
             commands.Count )
        {
            throw new ArgumentException( "Command IDs must be unique.", nameof( commands ) );
        }

        using var runtime = new CommandRuntime( commands.ToDictionary( command => command.Id, StringComparer.Ordinal ), diagnostics );
        ExtensionPeer? peer = null;

        await using var transport = CreateTransport( input, output, ( frame, token ) => runtime.HandleAsync( peer!, frame, token ) );

        peer = transport;
        peer.Start( );

        await peer
            .Completion.WaitAsync( cancellationToken )
            .ConfigureAwait( false );
    }

    private sealed class CommandRuntime( Dictionary<string, IExtensionCommand> commands, TextWriter diagnostics ) : IDisposable
    {
        private const int MaximumInputs = 32;
        private const int MaximumHostFeatures = 32;
        private const int MaximumInvocationIdCharacters = 128;

        private enum Phase { AwaitInitialize, AwaitInitialized, Ready, Opened, Closed }

        public void Dispose( ) => _viewWrites.Dispose( );

        private InitializeRequest? _initialization;
        private IExtensionCommand? _command;
        private Phase _phase;
        private long _generation;
        private readonly Dictionary<string, long> _revisions = new( StringComparer.Ordinal );
        private readonly SemaphoreSlim _viewWrites = new( 1, 1 );
        private readonly ExtensionSearchRuntime _search = new( commands.Values.ToArray( ), diagnostics );

        public async ValueTask<JsonElement> HandleAsync( ExtensionPeer peer, ExtensionFrame frame, CancellationToken cancellationToken )
        {
            if ( _phase == Phase.Closed )
            {
                throw new InvalidDataException( "The command session is closed." );
            }

            if ( _search.Active )
            {
                return await _search
                    .HandleAsync( frame, cancellationToken )
                    .ConfigureAwait( false );
            }

            switch ( frame.Method )
            {
                case ExtensionMethods.Initialize when _phase == Phase.AwaitInitialize:
                    return Initialize( frame );

                case ExtensionMethods.Initialized when _phase == Phase.AwaitInitialized:
                    EnableNegotiatedErrors( peer );
                    _phase = Phase.Ready;

                    return EmptyObject;

                case ExtensionMethods.CommandOpen when _phase == Phase.Ready:
                    return await OpenCommandAsync( peer, frame, cancellationToken )
                        .ConfigureAwait( false );

                case ExtensionMethods.CommandEvent when _phase == Phase.Opened:
                    return await HandleCommandEventAsync( peer, frame, cancellationToken )
                        .ConfigureAwait( false );

                case ExtensionMethods.Shutdown:
                    _phase = Phase.Closed;

                    return EmptyObject;

                default:
                    throw new InvalidDataException( "The command request is invalid in this session state." );
            }
        }

        private async ValueTask<JsonElement> OpenCommandAsync( ExtensionPeer peer, ExtensionFrame frame, CancellationToken cancellationToken )
        {
            _phase = Phase.Opened;
            var open = frame.Params?.Deserialize( ExtensionViewJsonContext.Default.CommandOpenRequest );
            if ( open is null )
            {
                throw new InvalidDataException( "Missing command input." );
            }

            if ( HasInvalidInputs( open ) )
            {
                throw new InvalidDataException( "Invalid command input." );
            }

            using ( var context = CreateContext( peer, open.Generation, open.InvocationId, open.Inputs, cancellationToken ) )
            {
                try
                {
                    await _command!
                        .OpenAsync( context, cancellationToken )
                        .ConfigureAwait( false );
                }
                catch ( Exception exception )
                {
                    SdkDiagnostics.Log( diagnostics, _command!.Id, nameof( IExtensionCommand.OpenAsync ), exception );

                    throw;
                }
            }

            return JsonSerializer.SerializeToElement(
                new CommandOutcome( _command!.Mode == ExtensionCommandModes.View ? ExtensionCommandOutcomes.OpenedView : ExtensionCommandOutcomes.Completed ),
                ExtensionViewJsonContext.Default.CommandOutcome );
        }

        private async ValueTask<JsonElement> HandleCommandEventAsync( ExtensionPeer peer, ExtensionFrame frame, CancellationToken cancellationToken )
        {
            var @event = frame.Params?.Deserialize( ExtensionViewJsonContext.Default.CommandEventRequest );
            if ( @event?.Event is null )
            {
                throw new InvalidDataException( "Missing command event." );
            }

            ViewValidation.ValidateId( @event.ViewId );

            if ( @event.ViewRevision < 1 )
            {
                throw new InvalidDataException( "Invalid displayed view revision." );
            }

            _revisions[@event.ViewId] = Math.Max( _revisions.GetValueOrDefault( @event.ViewId ), @event.ViewRevision );

            using ( var context = CreateContext( peer, @event.Generation, @event.InvocationId, [], cancellationToken ) )
            {
                try
                {
                    await _command!
                        .HandleAsync( context, @event.Event, cancellationToken )
                        .ConfigureAwait( false );
                }
                catch ( Exception exception )
                {
                    SdkDiagnostics.Log( diagnostics, _command!.Id, nameof( IExtensionCommand.HandleAsync ), exception );

                    throw;
                }
            }

            return JsonSerializer.SerializeToElement( new CommandOutcome( ExtensionCommandOutcomes.Completed ),
                ExtensionViewJsonContext.Default.CommandOutcome );
        }

        private void EnableNegotiatedErrors( ExtensionPeer peer )
        {
            if ( _initialization!.HostFeatures.Contains( ExtensionFeatures.ErrorCodes, StringComparer.Ordinal ) )
            {
                peer.EnableErrorData( );
            }
        }

        private JsonElement Initialize( ExtensionFrame frame )
        {
            var request = frame.Params?.Deserialize( ExtensionJsonContext.Default.InitializeRequest );
            if ( request is null )
            {
                throw new InvalidDataException( "Missing initialization." );
            }

            if ( request.Purpose is ExtensionPurposes.ScopedSearch or ExtensionPurposes.GlobalSearch )
            {
                return _search.Initialize( request );
            }

            if ( request.Protocol is not { Major: 1, Minor: 0 } ||
                 request.ExecutionEpoch < 1 ||
                 string.IsNullOrEmpty( request.SessionId ) ||
                 request.Purpose != ExtensionPurposes.Interactive ||
                 request.HostFeatures is not { Length: > 0 and <= MaximumHostFeatures } ||
                 !request.HostFeatures.Contains( ExtensionFeatures.Commands, StringComparer.Ordinal ) ||
                 request.Limits?.MaximumFrameBytes != ExtensionLimits.MaximumFrameBytes ||
                 !commands.TryGetValue( request.CommandId, out _command ) )
            {
                throw new InvalidDataException( "Unsupported command initialization." );
            }

            _initialization = request;
            _phase = Phase.AwaitInitialized;

            return CreateInitializationResponse( request );
        }

        private CommandContext CreateContext( ExtensionPeer peer, long generation, string invocation, Dictionary<string, JsonElement> inputs,
            CancellationToken cancellationToken )
        {
            if ( HasInvalidInvocation( generation, invocation ) )
            {
                throw new InvalidDataException( "The command invocation is stale or invalid." );
            }

            _generation = generation;

            return new CommandContext( peer, _initialization!, generation, invocation, inputs, _revisions,
                _viewWrites, _command!.Mode, cancellationToken );
        }

        private static bool HasInvalidInputs( CommandOpenRequest open )
        {
            return open.Inputs is null || open.Inputs.Count > MaximumInputs;
        }

        private bool HasInvalidInvocation( long generation, string invocation )
        {
            return generation <= _generation || string.IsNullOrWhiteSpace( invocation ) || invocation.Length > MaximumInvocationIdCharacters;
        }

        private JsonElement CreateInitializationResponse( InitializeRequest request )
        {
            return JsonSerializer.SerializeToElement(
                new InitializeResult( new( ), SdkVersion, commands.Keys.ToArray( ),
                    request.HostFeatures.Contains( ExtensionFeatures.ErrorCodes, StringComparer.Ordinal ) ? [ExtensionFeatures.ErrorCodes] : null ),
                ExtensionJsonContext.Default.InitializeResult );
        }
    }

    private static bool HasInvalidCommands( IReadOnlyList<IExtensionCommand> commands )
    {
        return commands.Count is < 1 or > ExtensionCommandLimits.MaximumCommands ||
               commands.Any( command => command is not { Mode: ExtensionCommandModes.View or ExtensionCommandModes.NoView } );
    }

    private static ExtensionPeer CreateTransport( Stream input, Stream output, Func<ExtensionFrame, CancellationToken, ValueTask<JsonElement>> handler )
    {
        return new( input, output, false, [
            ExtensionMethods.Initialize, ExtensionMethods.Initialized, ExtensionMethods.CommandOpen,
            ExtensionMethods.CommandEvent, ExtensionMethods.SearchQuery, ExtensionMethods.Shutdown
        ], handler );
    }
}

using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const int MaximumPickerOutputBytes = 16 * ByteUnits.BytesPerKibibyte;

    private static async Task<int> RunPickerAsync( CliArguments arguments, CancellationToken cancellationToken )
    {
        if ( !Console.IsInputRedirected )
        {
            Console.Error.WriteLine( "Pipe one choice per line into commanddeck pick." );

            return 2;
        }

        var bytes = await ReadPickerInputAsync( cancellationToken )
            .ConfigureAwait( false );

        var session = PickerSession.Parse( bytes, arguments.GetOption( CliOptionNames.Prompt ) ?? "Choose an item",
            arguments.HasFlag( CliOptionNames.LabelTab ) );

        if ( session.Choices.Count == 0 )
        {
            return 1;
        }

        var shell = Path.Combine( AppContext.BaseDirectory, "picker", "shell.qml" );
        if ( !File.Exists( shell ) )
        {
            throw new InvalidOperationException( "Picker UI assets are missing. Reinstall CommandDeck with its picker directory." );
        }

        if ( !ProcessRunner.ExistsOnPath( "qs" ) )
        {
            throw new InvalidOperationException( "The picker requires Quickshell (qs)." );
        }

        var directory = Directory.CreateTempSubdirectory( "commanddeck-pick-" ).FullName;

        try
        {
            if ( OperatingSystem.IsLinux( ) )
            {
                File.SetUnixFileMode( directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
            }

            return await RunPickerUiAsync( arguments, session, shell, directory, cancellationToken )
                .ConfigureAwait( false );
        }
        finally
        {
            Directory.Delete( directory, recursive: true );
        }
    }

    private static bool HasPickerFailed( ProcessResult result )
    {
        return result.ExitCode != 0 || result.TimedOut || result.AnyOutputLimitExceeded;
    }

    private static async Task<byte[]> ReadPickerInputAsync( CancellationToken cancellationToken )
    {
        var bytes = new byte[PickerLimits.MaximumInputBytes + 1];
        var count = 0;

        await using ( var input = Console.OpenStandardInput( ) )
        {
            while ( count < bytes.Length )
            {
                var read = await input
                    .ReadAsync( bytes.AsMemory( count ), cancellationToken )
                    .ConfigureAwait( false );

                if ( read == 0 )
                {
                    break;
                }

                count += read;
            }
        }

        return bytes[..count];
    }

    private static async Task<int> RunPickerUiAsync( CliArguments arguments, PickerSession session, string shell, string directory,
        CancellationToken cancellationToken )
    {
        var inputPath = Path.Combine( directory, "choices.json" );
        var outputPath = Path.Combine( directory, "selection" );

        await using ( var input = SafeRegularFile.CreatePrivateWriteStream( inputPath ) )
        {
            await JsonSerializer
                .SerializeAsync( input, session, CommandDeckJsonContext.Default.PickerSession, cancellationToken )
                .ConfigureAwait( false );
        }

        await using ( SafeRegularFile.CreatePrivateWriteStream( outputPath ) )
        {
        }

        var result = await StartPickerUiAsync( inputPath, outputPath, shell, directory, cancellationToken )
            .ConfigureAwait( false );

        if ( result.ExitCode == 1 )
        {
            return 1;
        }

        if ( HasPickerFailed( result ) )
        {
            Console.Error.WriteLine( "The picker could not complete. Check that the Wayland session and Quickshell are available." );

            return 2;
        }

        var selection = await File
            .ReadAllTextAsync( outputPath, cancellationToken )
            .ConfigureAwait( false );

        if ( !int.TryParse( selection, out var index ) || index < 0 || index >= session.Choices.Count )
        {
            Console.Error.WriteLine( "The picker returned no valid selection." );

            return 2;
        }

        Console.WriteLine( arguments.HasFlag( CliOptionNames.Index ) ? index.ToString( ) : session.Choices[index].Value );

        return 0;
    }

    private static async Task<ProcessResult> StartPickerUiAsync( string inputPath, string outputPath, string shell, string directory,
        CancellationToken cancellationToken )
    {
        return await ProcessRunner
            .RunBoundedAsync( "env", [
                    "COMMANDDECK_PICKER_INPUT=" + inputPath,
                    "COMMANDDECK_PICKER_OUTPUT=" + outputPath,
                    "qs",
                    "--no-color",
                    "-p",
                    shell
                ], directory, TimeSpan.FromMinutes( 30 ),
                new ProcessExecutionLimits
                {
                    MaximumStandardOutputBytes = MaximumPickerOutputBytes,
                    MaximumStandardErrorBytes = MaximumPickerOutputBytes,
                    MaximumMemoryBytes = long.MaxValue,
                    ClearEnvironment = false
                }, cancellationToken )
            .ConfigureAwait( false );
    }
}

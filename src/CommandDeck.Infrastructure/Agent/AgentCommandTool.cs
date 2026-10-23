using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

public sealed record AgentCommandPlan(
    string Executable,
    string ExecutableIdentity,
    IReadOnlyList<string> Argv,
    string WorkingDirectory,
    string EnvironmentProfile,
    IReadOnlyDictionary<string, string> Environment,
    string Digest );

public sealed record AgentCommandOutcome( int ExitCode, string Output, bool TimedOut, bool Truncated );

public sealed class AgentCommandTool( string? homeDirectory = null )
{
    private const int MaximumCommandErrorBytes = AgentLimits.MaximumCommandOutputBytes / 4;
    private const long MaximumCommandMemoryBytes = 4L * ByteUnits.BytesPerGibibyte;

    public const string MinimalProfile = "minimal";

    private static readonly IReadOnlyDictionary<string, string> MinimalEnvironment =
        ProcessEnvironmentDefaults.CreateEnvironment( ProcessEnvironmentDefaults.SystemExecutablePath );

    private readonly string _home = Path.GetFullPath( homeDirectory ?? Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ) );

    public AgentCommandPlan Plan( IReadOnlyList<string> argv, string? workingDirectory )
    {
        AgentContractValidator.ValidateArgv( argv );
        var executable = ResolveExecutable( argv[0] );
        var cwd = ResolveWorkingDirectory( workingDirectory );
        var identity = LinuxPathIdentity.TryGet( executable, out var token ) ? token : string.Empty;
        List<string> literal = [executable, .. argv.Skip( 1 )];
        var digest = ComputeDigest( identity, literal, cwd, MinimalProfile, MinimalEnvironment );

        return new AgentCommandPlan( executable, identity, literal, cwd, MinimalProfile, MinimalEnvironment,
            digest );
    }

    public static string ComputeDigest( string identity, IReadOnlyList<string> argv, string cwd, string profile,
        IReadOnlyDictionary<string, string> environment )
    {
        StringBuilder builder = new( );

        builder
            .Append( identity )
            .Append( '\n' );

        foreach ( var argument in argv )
        {
            builder
                .Append( argument )
                .Append( '\0' );
        }

        builder
            .Append( '\n' )
            .Append( cwd )
            .Append( '\n' )
            .Append( profile )
            .Append( '\n' );

        foreach ( (var key, var value) in environment.OrderBy( static pair => pair.Key, StringComparer.Ordinal ) )
        {
            builder
                .Append( key )
                .Append( '=' )
                .Append( value )
                .Append( '\0' );
        }

        return Convert
            .ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( builder.ToString( ) ) ) )
            .ToLowerInvariant( );
    }

    public static async ValueTask<AgentCommandOutcome> ExecuteAsync( AgentCommandPlan plan, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( plan );

        if ( HasExecutableIdentityChanged( plan ) )
        {
            throw new InvalidOperationException( "The executable changed after confirmation." );
        }

        var result = await ProcessRunner
            .RunBoundedAsync( plan.Executable, plan
                    .Argv.Skip( 1 )
                    .ToArray( ), plan.WorkingDirectory, TimeSpan.FromSeconds( AgentLimits.MaximumCommandSeconds ), CreateExecutionLimits( plan ),
                cancellationToken )
            .ConfigureAwait( false );

        var combined = result.StandardOutput;

        if ( !string.IsNullOrEmpty( result.StandardError ) )
        {
            combined = string.IsNullOrEmpty( combined ) ? result.StandardError : combined + "\n" + result.StandardError;
        }

        var sanitized = EscapeTerminalControls( combined );
        var truncated = result.OutputLimitExceeded || result.StandardErrorLimitExceeded;

        if ( Encoding.UTF8.GetByteCount( sanitized ) > AgentLimits.MaximumCommandOutputBytes )
        {
            sanitized = TakeUtf8Prefix( sanitized, AgentLimits.MaximumCommandOutputBytes );
            truncated = true;
        }

        return new AgentCommandOutcome( result.ExitCode, sanitized, result.TimedOut, truncated );
    }

    public static string EscapeTerminalControls( string value )
    {
        StringBuilder builder = new( value.Length );

        foreach ( var character in value )
        {
            if ( character is '\n' or '\t' )
            {
                builder.Append( character );
            }
            else if ( IsTerminalControl( character ) )
            {
                builder.Append( '\uFFFD' );
            }
            else
            {
                builder.Append( character );
            }
        }

        return builder.ToString( );
    }

    private static string TakeUtf8Prefix( string value, int maximumBytes )
    {
        var length = value.Length;
        while ( length > 0 && Encoding.UTF8.GetByteCount( value.AsSpan( 0, length ) ) > maximumBytes )
        {
            length--;

            if ( EndsWithHighSurrogate( value, length ) )
            {
                length--;
            }
        }

        return value[..length];
    }

    private static string ResolveExecutable( string command )
    {
        if ( IsInvalidCommand( command ) )
        {
            throw new InvalidOperationException( "The command name is invalid." );
        }

        var candidate = FindExecutableCandidate( command );
        if ( IsExecutableMissing( candidate ) )
        {
            throw new InvalidOperationException( "The command executable was not found." );
        }

        var canonical = CodexBinaryLocator.ResolveCanonical( candidate );
        if ( !HasExecutablePermissions( canonical ) )
        {
            throw new InvalidOperationException( "The command target is not executable." );
        }

        return canonical;
    }

    private static bool HasExecutablePermissions( string path )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            return false;
        }

        var mode = File.GetUnixFileMode( path );

        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private string ResolveWorkingDirectory( string? workingDirectory )
    {
        var candidate = GetWorkingDirectoryCandidate( workingDirectory );
        if ( !Directory.Exists( candidate ) )
        {
            throw new InvalidOperationException( "The working directory does not exist." );
        }

        return candidate;
    }

    private static bool HasExecutableIdentityChanged( AgentCommandPlan plan )
    {
        return !LinuxPathIdentity.TryGet( plan.Executable, out var current ) || !string.Equals( current, plan.ExecutableIdentity, StringComparison.Ordinal );
    }

    private static ProcessExecutionLimits CreateExecutionLimits( AgentCommandPlan plan )
    {
        return new ProcessExecutionLimits
        {
            MaximumStandardOutputBytes = AgentLimits.MaximumCommandOutputBytes,
            MaximumStandardErrorBytes = MaximumCommandErrorBytes,
            MaximumMemoryBytes = MaximumCommandMemoryBytes,
            EnvironmentVariables = plan.Environment
        };
    }

    private static bool IsTerminalControl( char character )
    {
        return char.IsControl( character ) || character is '\u2028' or '\u2029';
    }

    private static bool EndsWithHighSurrogate( string value, int length )
    {
        return length > 0 && char.IsHighSurrogate( value[length - 1] );
    }

    private static bool IsInvalidCommand( string command )
    {
        return string.IsNullOrWhiteSpace( command ) || command.Contains( '\0', StringComparison.Ordinal );
    }

    private static string? FindExecutableCandidate( string command )
    {
        if ( command.Contains( '/', StringComparison.Ordinal ) )
        {
            if ( Path.IsPathRooted( command ) )
            {
                return command;
            }

            return null;
        }

        return ProcessEnvironmentDefaults
            .SystemExecutablePath.Split( ':', StringSplitOptions.RemoveEmptyEntries )
            .Select( directory => Path.Combine( directory, command ) )
            .FirstOrDefault( File.Exists );
    }

    private string GetWorkingDirectoryCandidate( string? workingDirectory )
    {
        if ( string.IsNullOrWhiteSpace( workingDirectory ) )
        {
            return _home;
        }

        return AgentContextBroker.Canonicalize( workingDirectory, _home );
    }

    private static bool IsExecutableMissing( [NotNullWhen( false )] string? candidate )
    {
        return candidate is null || !File.Exists( candidate );
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CommandDeck;

public static class CodexBinaryLocator
{
    private const long MaximumVersionQueryMemoryBytes = 2L * ByteUnits.BytesPerGibibyte;
    private const int MaximumVersionOutputBytes = 4 * ByteUnits.BytesPerKibibyte;

    private const ushort GroupOrWorldWrite = 0x12;

    public static async ValueTask<CodexBinaryInfo> LocateAsync( AgentOptions options, CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( options );

        var candidate = options.CodexBinaryPath;
        var skippedLauncher = false;

        if ( candidate is null )
        {
            candidate = FindDirectCodexOnPath( out skippedLauncher );
        }

        if ( candidate is null )
        {
            throw new AiOperationException( AiErrorCategories.Unavailable, safeSummary: DescribeMissingBinary( skippedLauncher ) );
        }

        var canonical = ResolveCanonical( candidate );
        if ( IsLauncher( canonical ) )
        {
            throw new AiOperationException( AiErrorCategories.Unavailable,
                safeSummary: "The configured Codex path is a launcher shim. Set agent.codexBinaryPath to a direct Codex executable." );
        }

        var identity = ValidateOwnership( canonical );

        var version = await ReadVersionAsync( canonical, cancellationToken )
            .ConfigureAwait( false );

        EnsureSupported( version, options );

        return new CodexBinaryInfo( canonical, version, identity, CodexAppServerContract.IsReviewed( version ) );
    }

    public static string? FindOnPath( string executable )
    {
        var path = Environment.GetEnvironmentVariable( EnvironmentVariableNames.ExecutablePath );
        if ( string.IsNullOrEmpty( path ) )
        {
            return null;
        }

        foreach ( var directory in path.Split( ':', StringSplitOptions.RemoveEmptyEntries ) )
        {
            var candidate = Path.Combine( directory, executable );
            if ( File.Exists( candidate ) )
            {
                return candidate;
            }
        }

        return null;
    }

    public static string? LauncherWarning( AgentOptions options )
    {
        ArgumentNullException.ThrowIfNull( options );

        if ( options.CodexBinaryPath is { } configured )
        {
            try
            {
                return DescribeConfiguredBinaryFailure( configured );
            }
            catch ( AiOperationException )
            {
                return null;
            }
        }

        var direct = FindDirectCodexOnPath( out var skippedLauncher );

        if ( !skippedLauncher )
        {
            return null;
        }

        return DescribeLauncherResolution( direct );
    }

    private static string? FindDirectCodexOnPath( out bool skippedLauncher )
    {
        skippedLauncher = false;
        var path = Environment.GetEnvironmentVariable( EnvironmentVariableNames.ExecutablePath );
        if ( string.IsNullOrEmpty( path ) )
        {
            return null;
        }

        foreach ( var directory in path.Split( ':', StringSplitOptions.RemoveEmptyEntries ) )
        {
            var candidate = Path.Combine( directory, "codex" );
            if ( !File.Exists( candidate ) )
            {
                continue;
            }

            var canonical = ResolveCanonical( candidate );
            if ( IsLauncher( canonical ) )
            {
                skippedLauncher = true;

                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool IsLauncher( string canonicalPath ) =>
        Path.GetFileName( canonicalPath ) is "mise" or "rtx";

    public static string ResolveCanonical( string path )
    {
        try
        {
            FileInfo info = new( path );
            if ( !info.Exists )
            {
                throw new AiOperationException( AiErrorCategories.Unavailable, safeSummary: "The configured Codex binary does not exist." );
            }

            var target = info.ResolveLinkTarget( returnFinalTarget: true );

            return Path.GetFullPath( target?.FullName ?? info.FullName );
        }
        catch ( IOException )
        {
            throw new AiOperationException( AiErrorCategories.Unavailable, safeSummary: "The Codex binary link could not be resolved." );
        }
    }

    public static string ValidateOwnership( string canonicalPath )
    {
        using var file = SafeRegularFile.Open( canonicalPath, followLeafSymbolicLink: false );
        if ( !LinuxMountIdentity.TryGetOwnership( file.Handle, out var owner, out var permissions ) )
        {
            throw new AiOperationException( AiErrorCategories.Unavailable, safeSummary: "The Codex binary identity could not be read." );
        }

        if ( IsUntrustedOwner( owner ) )
        {
            throw new AiOperationException( AiErrorCategories.PolicyDenied, safeSummary: "The Codex binary is not owned by the current user or root." );
        }

        if ( (permissions & GroupOrWorldWrite) != 0 )
        {
            throw new AiOperationException( AiErrorCategories.PolicyDenied, safeSummary: "The Codex binary is group or world writable." );
        }

        return file.ReplacementIdentityToken;
    }

    public static bool HasSameIdentity( string canonicalPath, string identityToken )
    {
        try
        {
            using var file = SafeRegularFile.Open( canonicalPath, followLeafSymbolicLink: false );

            return string.Equals( file.ReplacementIdentityToken, identityToken, StringComparison.Ordinal );
        }
        catch ( InvalidOperationException )
        {
            return false;
        }
    }

    public static bool TryParseVersion( string? output, out Version version )
    {
        version = new Version( 0, 0, 0 );

        if ( string.IsNullOrWhiteSpace( output ) )
        {
            return false;
        }

        foreach ( var token in output.Split( [' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries ) )
        {
            var candidate = token.TrimStart( 'v' );
            if ( TryReadVersionToken( candidate, out var parsed ) )
            {
                version = parsed;

                return true;
            }
        }

        return false;
    }

    private static async ValueTask<string> ReadVersionAsync( string canonicalPath, CancellationToken cancellationToken )
    {
        var result = await ProcessRunner
            .RunBoundedAsync( canonicalPath, ["--version"], null, TimeSpan.FromSeconds( AgentLimits.StartupDeadlineSeconds ), CreateVersionQueryLimits( ),
                cancellationToken )
            .ConfigureAwait( false );

        if ( CannotReadSuccessfulVersion( result, out var version ) )
        {
            throw new AiOperationException( AiErrorCategories.Unavailable, safeSummary: "The Codex CLI did not report a usable version." );
        }

        return version.ToString( 3 );
    }

    private static void EnsureSupported( string version, AgentOptions options )
    {
        Version minimum = new( options.MinimumCodexMajorVersion, options.MinimumCodexMinorVersion, options.MinimumCodexPatchVersion );
        Version maximum = new( options.MaximumCodexMajorVersion, options.MaximumCodexMinorVersion, options.MaximumCodexPatchVersion );
        if ( IsOutsideVersionRange( version, minimum, maximum ) )
        {
            throw new AiOperationException( AiErrorCategories.Unsupported,
                safeSummary: string.Create( CultureInfo.InvariantCulture,
                    $"Codex CLI {version} is outside the supported {minimum.ToString( 3 )}-{maximum.ToString( 3 )} range." ) );
        }
    }

    private static string DescribeMissingBinary( bool skippedLauncher )
    {
        if ( skippedLauncher )
        {
            return "Codex on PATH is a launcher shim. Set agent.codexBinaryPath to a direct Codex executable.";
        }

        return "Codex was not found on PATH. Install it or set agent.codexBinaryPath.";
    }

    private static string? DescribeConfiguredBinaryFailure( string configured )
    {
        if ( IsLauncher( ResolveCanonical( configured ) ) )
        {
            return "The configured Codex path is a launcher shim. Set agent.codexBinaryPath to a direct Codex executable.";
        }

        return null;
    }

    private static string DescribeLauncherResolution( string? direct )
    {
        if ( direct is null )
        {
            return "Codex on PATH is a launcher shim. Set agent.codexBinaryPath to a direct Codex executable.";
        }

        return "CommandDeck skipped a Codex launcher shim. It found " + direct + ". Sign-in will check this executable.";
    }

    private static bool IsUntrustedOwner( uint owner )
    {
        return owner != LinuxMountIdentity.CurrentEffectiveUserId( ) && owner != 0;
    }

    private static ProcessExecutionLimits CreateVersionQueryLimits( )
    {
        return new ProcessExecutionLimits
        {
            MaximumStandardOutputBytes = MaximumVersionOutputBytes,
            MaximumStandardErrorBytes = MaximumVersionOutputBytes,
            MaximumMemoryBytes = MaximumVersionQueryMemoryBytes
        };
    }

    private static bool IsOutsideVersionRange( string version, Version minimum, Version maximum )
    {
        return !Version.TryParse( version, out var parsed ) || parsed < minimum || parsed > maximum;
    }

    private static bool TryReadVersionToken( string candidate, [NotNullWhen( true )] out Version? parsed )
    {
        parsed = default;

        return candidate.Length > 0 && char.IsAsciiDigit( candidate[0] ) && Version.TryParse( candidate, out parsed );
    }

    private static bool CannotReadSuccessfulVersion( ProcessResult result, [NotNullWhen( false )] out Version? version )
    {
        version = default;

        return result.ExitCode != 0 || !TryParseVersion( result.StandardOutput, out version );
    }
}

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CommandDeck;

public sealed record AgentContextAuthorization(
    bool Allowed,
    bool RequiresDecision,
    bool Protected,
    string CanonicalPath,
    string? ProposedFolder,
    string Reason );

public sealed record AgentDirectoryEntry( string Name, string Kind, long Size );

public sealed record AgentSearchMatch( string Path, int Line, string Text );

public sealed partial class AgentContextBroker
{
    private const string DirectoryEntryKind = "directory";
    private const int FileReadBufferBytes = 8 * ByteUnits.BytesPerKibibyte;
    private const string LinkEntryKind = "link";
    private const string FileEntryKind = "file";
    private const int MaximumDisplayNameCharacters = 240;
    private const int MaximumSearchQueryCharacters = 256;
    private const int GrantIdBytes = 16;
    private const int MaximumSearchDepth = 6;

    private static readonly string[] ProtectedRelativePaths =
    [
        ".ssh", ".gnupg", ".password-store", ".local/share/keyrings", ".config/gcloud", ".aws", ".azure",
        ".kube", ".docker", ".npmrc", ".pypirc", ".netrc", ".git-credentials", ".codex", ".claude",
        ".config/commanddeck", ".local/state/commanddeck", ".mozilla", ".config/chromium",
        ".config/google-chrome", ".config/BraveSoftware", ".gradle/gradle.properties", ".m2/settings.xml",
        ".cargo/credentials.toml", ".cargo/credentials", ".config/gh/hosts.yml", ".local/share/commanddeck",
        ".config/op", ".1password", ".config/Bitwarden", ".local/share/1password", ".config/sops",
        ".terraform.d/credentials.tfrc.json", ".config/hcloud", ".config/doctl", ".oci", ".config/rclone"
    ];

    private static readonly string[] ProtectedFileNames =
    [
        ".env", ".envrc", "credentials.json", "token.json", "secrets.json", "secrets.yaml", "secrets.yml"
    ];

    private static readonly string[] ProtectedExtensions = [".pem", ".key", ".kdbx", ".p12", ".pfx", ".ppk", ".asc"];

    private readonly AgentAuthority _authority;
    private readonly string _home;
    private readonly string[] _protectedDirectories;

    public AgentContextBroker( AgentAuthority authority, string? homeDirectory = null, AgentOptions? options = null )
    {
        if ( authority is null )
        {
            throw new ArgumentNullException( nameof( authority ) );
        }

        _authority = authority;
        _home = Path.TrimEndingDirectorySeparator( Path.GetFullPath( homeDirectory ?? Environment.GetFolderPath( Environment.SpecialFolder.UserProfile ) ) );

        var configured = options ?? new AgentOptions( );

        _protectedDirectories =
        [
            Path.TrimEndingDirectorySeparator( PathExpansion.Expand( configured.StateDirectory ) ),
            Path.TrimEndingDirectorySeparator( PathExpansion.Expand( configured.RuntimeDirectory ) )
        ];
    }

    public string HomeDirectory => _home;

    public static string Canonicalize( string requestedPath, string home )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace( requestedPath );

        if ( requestedPath.Contains( '\0', StringComparison.Ordinal ) )
        {
            throw new InvalidOperationException( "The path contains an invalid character." );
        }

        var expanded = ExpandHomePath( requestedPath, home );
        if ( !Path.IsPathRooted( expanded ) )
        {
            expanded = Path.Combine( home, expanded );
        }

        var full = Path.TrimEndingDirectorySeparator( Path.GetFullPath( expanded ) );

        return ResolveLinks( full );
    }

    public bool IsProtected( string canonicalPath ) =>
        IsProtectedPath( canonicalPath, _home ) ||
        _protectedDirectories.Any( directory =>
            IsWithinDirectory( canonicalPath, directory ) || IsWithinDirectory( canonicalPath, ResolveProtectedDirectory( directory ) ) );

    private static bool IsWithinDirectory( string path, string directory ) =>
        string.Equals( path, directory, StringComparison.Ordinal ) ||
        path.StartsWith( Path.EndsInDirectorySeparator( directory ) ? directory : directory + "/", StringComparison.Ordinal );

    // Resolve every existing ancestor, including storage whose leaf directory
    // has not yet been created by a backend.
    private static string ResolveProtectedDirectory( string directory )
    {
        var root = Path.GetPathRoot( directory )!;
        var resolved = root;

        foreach ( var segment in directory[root.Length..]
                     .Split( Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries ) )
        {
            resolved = Path.Combine( resolved, segment );

            if ( Directory.Exists( resolved ) )
            {
                resolved = ResolveLinks( resolved );
            }
        }

        return Path.TrimEndingDirectorySeparator( resolved );
    }

    internal static bool IsProtectedPath( string canonicalPath, string home )
    {
        var relative = Path.GetRelativePath( home, canonicalPath );
        if ( relative.StartsWith( "..", StringComparison.Ordinal ) )
        {
            return false;
        }

        foreach ( var protectedPath in ProtectedRelativePaths )
        {
            if ( IsProtectedDescendant( relative, protectedPath ) )
            {
                return true;
            }
        }

        var name = Path.GetFileName( canonicalPath );
        if ( IsProtectedFileName( name ) )
        {
            return true;
        }

        var extension = Path.GetExtension( name );

        return ProtectedExtensions.Contains( extension, StringComparer.OrdinalIgnoreCase );
    }

    public async ValueTask<AgentContextAuthorization> AuthorizeAsync( string requestedPath, string operation, AgentPreferences preferences,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( preferences );

        string canonical;

        try
        {
            canonical = Canonicalize( requestedPath, _home );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException or ArgumentException )
        {
            return new AgentContextAuthorization( false, false, false, requestedPath, null, "The path cannot be resolved." );
        }

        if ( preferences.TrustLevel < AgentTrustLevels.ApprovedContext )
        {
            return new AgentContextAuthorization( false, false, false, canonical, null, "File context is not enabled at this trust level." );
        }

        if ( !IsUnderHome( canonical ) )
        {
            return new AgentContextAuthorization( false, false, false, canonical, null, "Only paths under your home directory are readable." );
        }

        var protectedTarget = IsProtected( canonical );

        // Unrestricted removes every safety layer, protected paths included:
        // the user typed the phrase and holds a lease. Every lower level keeps
        // protected paths behind an explicit one-time override.
        if ( preferences.TrustLevel >= AgentTrustLevels.Unrestricted )
        {
            return new AgentContextAuthorization( true, false, protectedTarget, canonical, null,
                protectedTarget ? "Unrestricted access includes protected paths." : "Unrestricted access is enabled." );
        }

        if ( protectedTarget )
        {
            return new AgentContextAuthorization( false, true, true, canonical, null, "This path holds credentials or private configuration." );
        }

        if ( preferences.TrustLevel >= AgentTrustLevels.ReadEntireHome )
        {
            return new AgentContextAuthorization( true, false, false, canonical, null, "Read entire home is enabled." );
        }

        if ( await TryConfirmApprovedFolderGrantAsync( preferences, canonical, cancellationToken )
                .ConfigureAwait( false ) )
        {
            return new AgentContextAuthorization( true, false, false, canonical, null, "Inside an approved folder." );
        }

        var proposed = operation == AgentContextOperations.ReadFile ? Path.GetDirectoryName( canonical ) ?? canonical : canonical;

        return new AgentContextAuthorization( false, true, false, canonical, proposed, "Approval is required for this folder." );
    }

    public async ValueTask<AgentGrant> GrantFolderAsync( string canonicalFolder, CancellationToken cancellationToken )
    {
        if ( IsGrantFolderInvalid( canonicalFolder ) )
        {
            throw new InvalidOperationException( "The folder cannot be approved." );
        }

        var identity = LinuxPathIdentity.TryGet( canonicalFolder, out var token ) ? token : string.Empty;
        AgentGrant grant = CreateFolderGrant( canonicalFolder, identity );

        return await _authority
            .AddGrantAsync( grant, cancellationToken )
            .ConfigureAwait( false );
    }

    public async ValueTask<bool> TryConfirmGrantAsync( string canonicalPath, CancellationToken cancellationToken )
    {
        var grants = await _authority
            .GetGrantsAsync( cancellationToken )
            .ConfigureAwait( false );

        foreach ( var grant in grants )
        {
            if ( IsInactiveFolderGrant( grant ) )
            {
                continue;
            }

            if ( IsOutsideGrant( canonicalPath, grant ) )
            {
                continue;
            }

            if ( HasGrantIdentityChanged( grant ) )
            {
                await _authority
                    .MarkGrantStateAsync( grant.Id, AgentGrantStates.Stale, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds( ), cancellationToken )
                    .ConfigureAwait( false );

                continue;
            }

            return true;
        }

        return false;
    }

    private bool IsUnderHome( string canonicalPath ) =>
        string.Equals( canonicalPath, _home, StringComparison.Ordinal ) || canonicalPath.StartsWith( _home + "/", StringComparison.Ordinal );

    private SafeRegularFile OpenAuthorizedFile( string expectedPath )
    {
        var file = SafeRegularFile.Open( expectedPath, followLeafSymbolicLink: false );

        try
        {
            EnsureOpenedPath( expectedPath, file.CanonicalPath );

            return file;
        }
        catch
        {
            file.Dispose( );

            throw;
        }
    }

    private OpenedDirectory OpenAuthorizedDirectory( string expectedPath )
    {
        var descriptor = OpenNative( Path.GetFullPath( expectedPath ),
            LinuxConstants.OReadOnly | LinuxConstants.ONonBlocking | LinuxConstants.ODirectory | LinuxConstants.OCloseOnExec | LinuxConstants.ONoFollow );

        if ( descriptor < 0 )
        {
            throw new InvalidOperationException( "The directory cannot be opened safely." );
        }

        SafeFileHandle handle = new( ( IntPtr ) descriptor, ownsHandle: true );

        try
        {
            var target = Directory.ResolveLinkTarget( $"/proc/self/fd/{descriptor}", returnFinalTarget: true );
            if ( target is null )
            {
                throw new InvalidOperationException( "The directory identity cannot be resolved safely." );
            }

            var canonical = Path.TrimEndingDirectorySeparator( Path.GetFullPath( target.FullName ) );
            EnsureOpenedPath( expectedPath, canonical );

            return new OpenedDirectory( handle, canonical );
        }
        catch
        {
            handle.Dispose( );

            throw;
        }
    }

    private void EnsureOpenedPath( string expectedPath, string openedPath )
    {
        var expected = Path.TrimEndingDirectorySeparator( Path.GetFullPath( expectedPath ) );
        if ( HasOpenedPathChanged( openedPath, expected ) )
        {
            throw new InvalidOperationException( "The authorized path changed before it could be opened." );
        }
    }

    private static string ResolveLinks( string fullPath )
    {
        FileSystemInfo info = Directory.Exists( fullPath ) ? new DirectoryInfo( fullPath ) : new FileInfo( fullPath );
        if ( !info.Exists )
        {
            return fullPath;
        }

        var target = info.ResolveLinkTarget( returnFinalTarget: true );

        return target is null ? fullPath : Path.TrimEndingDirectorySeparator( Path.GetFullPath( target.FullName ) );
    }

    private static string Digest( string value ) =>
        Convert
            .ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( value ) ) )
            .ToLowerInvariant( );

    private sealed class OpenedDirectory( SafeFileHandle handle, string canonicalPath ) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public string CanonicalPath { get; } = canonicalPath;
        public string DescriptorPath => $"/proc/{Environment.ProcessId}/fd/{checked(( int ) Handle.DangerousGetHandle( ))}";
        public void Dispose( ) => Handle.Dispose( );
    }

    [LibraryImport( NativeLibraryNames.LibC, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true )]
    private static partial int OpenNative( string path, int flags );

    private static string ExpandHomePath( string requestedPath, string home )
    {
        if ( requestedPath == "~" )
        {
            return home;
        }

        if ( requestedPath.StartsWith( "~/", StringComparison.Ordinal ) )
        {
            return Path.Combine( home, requestedPath[2..] );
        }

        return requestedPath;
    }

    private static bool IsProtectedDescendant( string relative, string protectedPath )
    {
        return string.Equals( relative, protectedPath, StringComparison.Ordinal ) || relative.StartsWith( protectedPath + "/", StringComparison.Ordinal );
    }

    private static bool IsProtectedFileName( string name )
    {
        return ProtectedFileNames.Contains( name, StringComparer.Ordinal ) ||
               name.StartsWith( ".env.", StringComparison.Ordinal ) ||
               name.StartsWith( "id_", StringComparison.Ordinal );
    }

    private async ValueTask<bool> TryConfirmApprovedFolderGrantAsync( AgentPreferences preferences, string canonical, CancellationToken cancellationToken )
    {
        return preferences.ContextPolicy == AgentContextPolicies.ApprovedFolders &&
               await TryConfirmGrantAsync( canonical, cancellationToken )
                   .ConfigureAwait( false );
    }

    private bool IsGrantFolderInvalid( string canonicalFolder )
    {
        return !Directory.Exists( canonicalFolder ) || !IsUnderHome( canonicalFolder ) || IsProtected( canonicalFolder );
    }

    private static AgentGrant CreateFolderGrant( string canonicalFolder, string identity )
    {
        return new( )
        {
            Id = Convert
                .ToHexString( RandomNumberGenerator.GetBytes( GrantIdBytes ) )
                .ToLowerInvariant( ),
            Kind = AgentGrantKinds.Folder,
            Scope = canonicalFolder,
            Identity = identity,
            PayloadDigest = Digest( canonicalFolder + "\n" + identity ),
            State = AgentGrantStates.Active,
            Label = Path.GetFileName( canonicalFolder )
        };
    }

    private static bool IsInactiveFolderGrant( AgentGrant grant )
    {
        return grant is not { Kind: AgentGrantKinds.Folder, State: AgentGrantStates.Active };
    }

    private static bool IsOutsideGrant( string canonicalPath, AgentGrant grant )
    {
        return !string.Equals( canonicalPath, grant.Scope, StringComparison.Ordinal ) &&
               !canonicalPath.StartsWith( grant.Scope + "/", StringComparison.Ordinal );
    }

    private static bool HasGrantIdentityChanged( AgentGrant grant )
    {
        return !string.IsNullOrEmpty( grant.Identity ) &&
               (!LinuxPathIdentity.TryGet( grant.Scope, out var current ) || !string.Equals( current, grant.Identity, StringComparison.Ordinal ));
    }

    private bool HasOpenedPathChanged( string openedPath, string expected )
    {
        return !IsUnderHome( openedPath ) || !string.Equals( expected, openedPath, StringComparison.Ordinal );
    }
}

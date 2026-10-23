using System.IO.Compression;
using System.Security.Cryptography;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck;

public sealed class StagedExtension : IAsyncDisposable
{
    internal StagedExtension( string stagingDirectory, string digest, ExtensionManifest manifest )
    {
        StagingDirectory = stagingDirectory;
        Digest = digest;
        Manifest = manifest;
    }

    public string StagingDirectory { get; }
    public string Digest { get; }
    public ExtensionManifest Manifest { get; }

    public string Materialize( string artifactRoot, string installationKey )
    {
        if ( IsInstallationKeyInvalid( installationKey ) )
        {
            throw new ArgumentException( "The installation key is invalid.", nameof( installationKey ) );
        }

        var versions = ExtensionArtifactStore.VersionsDirectoryFor( artifactRoot, installationKey );
        ExtensionArchive.CreatePrivateDirectory( versions );
        var destination = ExtensionArtifactStore.VersionDirectoryFor( artifactRoot, installationKey, Digest );
        if ( !Directory.Exists( destination ) )
        {
            Directory.Move( Path.Combine( StagingDirectory, "content" ), destination );
        }

        // Persist the rename and each newly created ancestor before the database
        // can refer to this artifact. A surviving activation must have its files.
        LinuxDirectoryDurability.FlushParent( destination );
        LinuxDirectoryDurability.FlushParent( versions );
        LinuxDirectoryDurability.FlushParent( Path.GetDirectoryName( versions )! );
        LinuxDirectoryDurability.FlushParent( Path.GetFullPath( artifactRoot ) );

        return destination;
    }

    public ValueTask DisposeAsync( )
    {
        if ( Directory.Exists( StagingDirectory ) )
        {
            Directory.Delete( StagingDirectory, recursive: true );
        }

        return ValueTask.CompletedTask;
    }

    private static bool IsInstallationKeyInvalid( string installationKey )
    {
        return installationKey.Length != 64 || installationKey.Any( c => !char.IsAsciiHexDigit( c ) );
    }
}

public static class ExtensionArchive
{
    private const int CopyBufferBytes = 65536;
    private const int ElfHeaderBytes = 64;
    private const int ElfReadBufferBytes = 4096;
    private const int ElfTypeOffset = 16;
    private const int ElfMachineOffset = 18;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixDirectoryType = 0x4000;
    private const int UnixRegularFileType = 0x8000;
    private const int UnixSpecialModeMask = 0xE00;
    private const int UnixExecuteMask = 0x49;
    private const int MaximumCompressionRatio = 200;
    private const int MinimumExpansionAllowanceBytes = ByteUnits.BytesPerMebibyte;
    public const long MaximumCompressedBytes = 200L * ByteUnits.BytesPerMebibyte;

    public static async ValueTask<StagedExtension> StageAsync( string archivePath, string artifactRoot, CancellationToken cancellationToken,
        string? expectedDigest = null )
    {
        CreatePrivateDirectory( artifactRoot );

        var staging = ExtensionArtifactStore.StagingDirectoryFor( artifactRoot, Guid
            .NewGuid( )
            .ToString( "N" ) );

        CreatePrivateDirectory( staging );

        try
        {
            var copy = Path.Combine( staging, "package.cdext" );

            var digest = await CopyArchiveAsync( archivePath, copy, cancellationToken )
                .ConfigureAwait( false );

            if ( HasDigestMismatch( expectedDigest, digest ) )
            {
                throw new InvalidDataException( "The extension archive does not match its expected digest." );
            }

            using var archive = ZipFile.OpenRead( copy );
            ValidateEntries( archive );
            var entry = archive.GetEntry( CommandDeck.ExtensionProtocol.ExtensionPackageFiles.Manifest );
            if ( entry is null )
            {
                throw new InvalidDataException( "The archive has no root manifest." );
            }

            if ( entry.Length > ManifestValidation.MaximumManifestBytes )
            {
                throw new InvalidDataException( "The manifest is too large." );
            }

            using MemoryStream manifestBytes = new( );

            await using ( var input = entry.Open( ) )
            {
                await CopyBoundedAsync( input, manifestBytes, ManifestValidation.MaximumManifestBytes, cancellationToken )
                    .ConfigureAwait( false );
            }

            var manifest = ManifestValidation.Parse( manifestBytes.ToArray( ) );
            var content = Path.Combine( staging, "content" );
            CreatePrivateDirectory( content );

            await ExtractAsync( archive, content, manifest, cancellationToken )
                .ConfigureAwait( false );

            return new StagedExtension( staging, digest, manifest );
        }
        catch
        {
            Directory.Delete( staging, recursive: true );

            throw;
        }
    }

    private static async Task<string> CopyArchiveAsync( string source, string destination, CancellationToken cancellationToken )
    {
        await using var input = new FileStream( source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan );

        if ( input.Length > MaximumCompressedBytes )
        {
            throw new InvalidDataException( "The extension archive exceeds 200 MiB." );
        }

        await using var output = new FileStream( destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, CopyBufferBytes,
            FileOptions.Asynchronous );

        await CopyBoundedAsync( input, output, MaximumCompressedBytes, cancellationToken )
            .ConfigureAwait( false );

        await output
            .FlushAsync( cancellationToken )
            .ConfigureAwait( false );

        output.Flush( flushToDisk: true );
        output.Position = 0;

        return Convert
            .ToHexString( await SHA256
                .HashDataAsync( output, cancellationToken )
                .ConfigureAwait( false ) )
            .ToLowerInvariant( );
    }

    private static void ValidateEntries( ZipArchive archive )
    {
        if ( archive.Entries.Count is < 1 or > ExtensionPackageLimits.MaximumArchiveEntries )
        {
            throw new InvalidDataException( "The archive file count is invalid." );
        }

        HashSet<string> paths = new( StringComparer.OrdinalIgnoreCase );
        long expanded = 0;

        foreach ( var entry in archive.Entries )
        {
            var directory = entry.FullName.EndsWith( '/' );
            var name = directory ? entry.FullName[..^1] : entry.FullName;
            ManifestValidation.ValidatePackagePath( name );

            if ( !paths.Add( name ) )
            {
                throw new InvalidDataException( "The archive contains duplicate or colliding paths." );
            }

            var type = (entry.ExternalAttributes >> ZipEntryAttributes.UnixModeShift) & UnixFileTypeMask;
            if ( HasInvalidEntryType( entry, directory, type ) )
            {
                throw new InvalidDataException( "The archive contains a link, special file, or unsupported permissions." );
            }

            expanded = checked(expanded + entry.Length);

            if ( ExceedsExpansionLimit( expanded, entry, directory ) )
            {
                throw new InvalidDataException( "The archive exceeds its expansion limits." );
            }
        }

        var files = archive
            .Entries.Where( entry => !entry.FullName.EndsWith( '/' ) )
            .Select( entry => entry.FullName )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );

        foreach ( var name in paths )
        {
            var parent = name;
            while ( parent.LastIndexOf( '/' ) is var slash && slash > 0 )
            {
                parent = parent[..slash];

                if ( files.Contains( parent ) )
                {
                    throw new InvalidDataException( "An archive file is also used as a directory." );
                }
            }
        }
    }

    private static async Task ExtractAsync( ZipArchive archive, string destination, ExtensionManifest manifest, CancellationToken cancellationToken )
    {
        var entrypoints = manifest
            .Targets.Values.Select( target => target.Entrypoint )
            .ToHashSet( StringComparer.Ordinal );

        long remaining = ExtensionPackageLimits.MaximumExpandedBytes;

        foreach ( var entry in archive.Entries )
        {
            cancellationToken.ThrowIfCancellationRequested( );

            if ( entry.FullName.EndsWith( '/' ) )
            {
                continue;
            }

            var path = Path.Combine( destination, entry.FullName );
            CreatePrivateDirectory( Path.GetDirectoryName( path )! );

            var executable = ((entry.ExternalAttributes >> ZipEntryAttributes.UnixModeShift) & UnixExecuteMask) != 0;
            if ( IsExecutableOutsideTarget( manifest, entry, executable ) )
            {
                throw new InvalidDataException( "The archive has an executable outside its target directories." );
            }

            await using ( var input = entry.Open( ) )
            {
                await using ( var output = new FileStream( path, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes,
                                 FileOptions.Asynchronous ) )
                {
                    var written = await CopyBoundedAsync( input, output, Math.Min( entry.Length, remaining ), cancellationToken )
                        .ConfigureAwait( false );

                    if ( written != entry.Length )
                    {
                        throw new InvalidDataException( "The archive entry size is inconsistent." );
                    }

                    remaining -= written;
                    output.Flush( flushToDisk: true );
                }
            }

            if ( OperatingSystem.IsLinux( ) )
            {
                File.SetUnixFileMode( path, UnixFileMode.UserRead | (entrypoints.Contains( entry.FullName ) ? UnixFileMode.UserExecute : 0) );
            }
        }

        foreach ( var entrypoint in entrypoints )
        {
            if ( !File.Exists( Path.Combine( destination, entrypoint ) ) )
            {
                throw new InvalidDataException( "A declared executable is missing." );
            }
        }

        foreach ( var (rid, target) in manifest.Targets )
        {
            await ValidateElfAsync( Path.Combine( destination, target.Entrypoint ), rid, cancellationToken )
                .ConfigureAwait( false );
        }

        foreach ( var command in manifest.Commands )
        {
            if ( IsIconMissing( destination, command ) )
            {
                throw new InvalidDataException( "A declared icon is missing." );
            }
        }

        foreach ( var directory in Directory
                     .EnumerateDirectories( destination, "*", SearchOption.AllDirectories )
                     .OrderByDescending( path => path.Length ) )
        {
            LinuxDirectoryDurability.FlushParent( Path.Combine( directory, "." ) );
        }

        LinuxDirectoryDurability.FlushParent( Path.Combine( destination, "." ) );
    }

    private static async Task ValidateElfAsync( string path, string rid, CancellationToken cancellationToken )
    {
        await using var input = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read, ElfReadBufferBytes, FileOptions.Asynchronous );
        var header = new byte[ElfHeaderBytes];

        await input
            .ReadExactlyAsync( header, cancellationToken )
            .ConfigureAwait( false );

        var machine = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( ElfMachineOffset, 2 ) );
        var kind = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( ElfTypeOffset, 2 ) );
        if ( IsElfHeaderInvalid( rid, header, machine, kind ) )
        {
            throw new InvalidDataException( "The executable is not a 64-bit ELF target for its declared architecture." );
        }
    }

    private static async Task<long> CopyBoundedAsync( Stream input, Stream output, long maximum, CancellationToken cancellationToken )
    {
        var buffer = new byte[CopyBufferBytes];
        long total = 0;
        int count;
        while ( (count = await input
                    .ReadAsync( buffer, cancellationToken )
                    .ConfigureAwait( false )) >
                0 )
        {
            total += count;

            if ( total > maximum )
            {
                throw new InvalidDataException( "The extension content exceeds its byte limit." );
            }

            await output
                .WriteAsync( buffer.AsMemory( 0, count ), cancellationToken )
                .ConfigureAwait( false );
        }

        return total;
    }

    internal static void CreatePrivateDirectory( string path )
    {
        if ( OperatingSystem.IsLinux( ) )
        {
            Directory.CreateDirectory( path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
        }
        else
        {
            Directory.CreateDirectory( path );
        }
    }

    private static bool HasDigestMismatch( string? expectedDigest, string digest )
    {
        return expectedDigest is not null && !string.Equals( digest, expectedDigest, StringComparison.OrdinalIgnoreCase );
    }

    private static bool HasInvalidEntryType( ZipArchiveEntry entry, bool directory, int type )
    {
        return type != 0 && type != (directory ? UnixDirectoryType : UnixRegularFileType) ||
               ((entry.ExternalAttributes >> ZipEntryAttributes.UnixModeShift) & UnixSpecialModeMask) != 0;
    }

    private static bool ExceedsExpansionLimit( long expanded, ZipArchiveEntry entry, bool directory )
    {
        return expanded > ExtensionPackageLimits.MaximumExpandedBytes ||
               directory && entry.Length != 0 ||
               entry.Length > Math.Max( MinimumExpansionAllowanceBytes, entry.CompressedLength * MaximumCompressionRatio );
    }

    private static bool IsExecutableOutsideTarget( ExtensionManifest manifest, ZipArchiveEntry entry, bool executable )
    {
        return executable && !manifest.Targets.Keys.Any( rid => entry.FullName.StartsWith( "bin/" + rid + "/", StringComparison.Ordinal ) );
    }

    private static bool IsIconMissing( string destination, ExtensionCommand command )
    {
        return command.Icon is not null && !File.Exists( Path.Combine( destination, command.Icon ) );
    }

    private static bool IsElfHeaderInvalid( string rid, byte[] header, ushort machine, ushort kind )
    {
        return !header
                   .AsSpan( 0, 4 )
                   .SequenceEqual( ExtensionTargets.ElfMagic ) ||
               header[4] != 2 ||
               header[5] != 1 ||
               header[6] != 1 ||
               kind is not (2 or 3) ||
               machine != (rid == ExtensionTargets.LinuxX64 ? ExtensionTargets.ElfX64 : ExtensionTargets.ElfArm64);
    }
}

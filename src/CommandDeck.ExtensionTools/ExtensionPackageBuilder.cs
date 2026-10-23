using System.IO.Compression;
using System.Text.Json;
using CommandDeck.ExtensionProtocol;
using CommandDeck.Extensions.Views;

namespace CommandDeck.ExtensionTools;

public static class ExtensionPackageBuilder
{
    private const int UnixModeShift = 16;
    private const int UnixRegularFile = 0x8000;

    // rwxr-xr-x and rw-r--r--.
    private const int ExecutableFileMode = 0x1ed;
    private const int NonExecutableFileMode = 0x1a4;

    // A fixed entry time keeps package bytes reproducible.
    private static readonly DateTimeOffset ReproducibleEntryTime = new( 2026, 1, 1, 0, 0, 0,
        TimeSpan.Zero );

    private const int MaximumContentFiles = ExtensionPackageLimits.MaximumArchiveEntries - 1;

    public static void Pack( string manifestPath, string publishDirectory, string outputPath )
    {
        var manifestBytes = File.ReadAllBytes( manifestPath );
        _ = ManifestValidation.Parse( manifestBytes );
        using var manifest = JsonDocument.Parse( manifestBytes );
        var entrypoint = manifest
            .RootElement.GetProperty( "targets" )
            .GetProperty( ExtensionTargets.LinuxX64 )
            .GetProperty( "entrypoint" )
            .GetString( )!;

        if ( HasInvalidEntrypoint( entrypoint ) )
        {
            throw new InvalidDataException( "This preview packer requires an entrypoint directly under bin/linux-x64." );
        }

        var source = publishDirectory;
        var files = new List<string>( );
        CollectPublishedFiles( source, files );
        var assetsDirectory = Path.Combine( Path.GetDirectoryName( manifestPath )!, ExtensionPackageFiles.AssetsDirectory );
        var assets = CollectAssets( assetsDirectory );
        var binary = Path.Combine( source, Path.GetFileName( entrypoint ) );
        using ( var input = File.OpenRead( binary ) )
        {
            Span<byte> magic = stackalloc byte[4];
            input.ReadExactly( magic );

            if ( !magic.SequenceEqual( ExtensionTargets.ElfMagic ) )
            {
                throw new InvalidDataException( "Publish the declared ELF executable before packing." );
            }
        }

        files.Sort( StringComparer.Ordinal );
        ValidatePackageLimits( manifestBytes, files, assets );
        Directory.CreateDirectory( Path.GetDirectoryName( outputPath )! );

        using ( var output = new FileStream( outputPath, FileMode.CreateNew ) )
        {
            using ( var archive = new ZipArchive( output, ZipArchiveMode.Create ) )
            {
                using ( var target = Entry( archive, CommandDeck.ExtensionProtocol.ExtensionPackageFiles.Manifest, false )
                           .Open( ) )
                {
                    target.Write( manifestBytes );
                }

                foreach ( var file in files )
                {
                    using var input = File.OpenRead( file );

                    using var target = Entry( archive, ExtensionPackageFiles.Entrypoint( ExtensionTargets.LinuxX64, Path
                            .GetRelativePath( source, file )
                            .Replace( '\\', '/' ) ), OperatingSystem.IsLinux( ) && (File.GetUnixFileMode( file ) & UnixFileMode.UserExecute) != 0 )
                        .Open( );

                    input.CopyTo( target );
                }

                foreach ( var file in assets.Order( StringComparer.Ordinal ) )
                {
                    using var input = File.OpenRead( file );

                    using var target = Entry( archive, ExtensionPackageFiles.AssetPrefix + Path.GetFileName( file ), false )
                        .Open( );

                    input.CopyTo( target );
                }
            }
        }
    }

    private static void CollectPublishedFiles( string directory, List<string> files )
    {
        if ( (File.GetAttributes( directory ) & FileAttributes.ReparsePoint) != 0 )
        {
            throw new InvalidDataException( "Symlinks are not package inputs." );
        }

        foreach ( var path in Directory.EnumerateFileSystemEntries( directory ) )
        {
            var attributes = File.GetAttributes( path );
            if ( (attributes & FileAttributes.ReparsePoint) != 0 )
            {
                throw new InvalidDataException( "Symlinks are not package inputs." );
            }

            if ( (attributes & FileAttributes.Directory) != 0 )
            {
                CollectPublishedFiles( path, files );
            }
            else if ( Path.GetExtension( path ) is not (".dbg" or ".pdb") )
            {
                files.Add( path );
            }
        }
    }

    static ZipArchiveEntry Entry( ZipArchive archive, string name, bool executable )
    {
        var entry = archive.CreateEntry( name, CompressionLevel.Optimal );
        entry.LastWriteTime = ReproducibleEntryTime;
        entry.ExternalAttributes = (UnixRegularFile | (executable ? ExecutableFileMode : NonExecutableFileMode)) << UnixModeShift;

        return entry;
    }

    public static string[] CollectAssets( string directory )
    {
        if ( !Directory.Exists( directory ) )
        {
            return [];
        }

        ValidateAssetDirectory( directory );
        var assets = Directory.GetFiles( directory );
        foreach ( var asset in assets )
        {
            ValidateAsset( asset );
        }

        return assets;
    }

    private static bool HasInvalidEntrypoint( string entrypoint )
    {
        return entrypoint.Contains( '\\' ) ||
               Path.GetDirectoryName( entrypoint ) != ExtensionPackageFiles.BinaryDirectory( ExtensionTargets.LinuxX64 ) ||
               Path.GetFileName( entrypoint ) is ".." or "." or "";
    }

    private static void ValidatePackageLimits( byte[] manifestBytes, List<string> files, string[] assets )
    {
        if ( files.Count + assets.Length > MaximumContentFiles ||
             TotalPackageBytes( manifestBytes, files, assets ) > ExtensionPackageLimits.MaximumExpandedBytes )
        {
            throw new InvalidDataException( "Published output exceeds the package limits." );
        }
    }

    private static long TotalPackageBytes( byte[] manifestBytes, List<string> files, string[] assets )
    {
        return files
                   .Concat( assets )
                   .Sum( file => new FileInfo( file ).Length ) +
               manifestBytes.Length;
    }

    private static void ValidateAssetDirectory( string directory )
    {
        if ( (File.GetAttributes( directory ) & FileAttributes.ReparsePoint) != 0 ||
             Directory.GetDirectories( directory )
                 .Length >
             0 )
        {
            throw new InvalidDataException( "Assets must be regular files in one flat directory." );
        }
    }

    private static void ValidateAsset( string asset )
    {
        if ( (File.GetAttributes( asset ) & FileAttributes.ReparsePoint) != 0 ||
             Path.GetExtension( asset ) != ".png" ||
             new FileInfo( asset ).Length > ExtensionViewLimits.MaximumPngAssetBytes )
        {
            throw new InvalidDataException( "Assets must be regular PNG files no larger than 32 KiB." );
        }
    }
}

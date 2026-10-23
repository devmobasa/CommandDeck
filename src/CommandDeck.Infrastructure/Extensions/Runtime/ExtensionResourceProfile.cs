using System.Globalization;

namespace CommandDeck;

/// <summary>One resource policy used both to configure and verify the systemd boundary.</summary>
internal sealed record ExtensionResourceProfile( long MemoryBytes, int Tasks, int CpuQuotaPercent, int? RuntimeMaxSeconds )
{
    public static ExtensionResourceProfile Interactive { get; } = new( 512L << 20, 64, 100, 1800 );
    public static ExtensionResourceProfile Search { get; } = new( 128L << 20, 32, 50, 5 );
    public static ExtensionResourceProfile PackageSlice { get; } = new( 512L << 20, 64, 100, null );
    public static ExtensionResourceProfile GlobalSlice { get; } = new( 1L << 30, 256, 200, null );

    public string[] SystemdProperties( )
    {
        var memory = FormatMemoryLimit( );

        List<string> properties =
        [
            "MemoryMax=" + memory, "MemorySwapMax=0", "TasksMax=" + Tasks.ToString( CultureInfo.InvariantCulture ),
            "CPUQuota=" + CpuQuotaPercent.ToString( CultureInfo.InvariantCulture ) + "%"
        ];

        if ( RuntimeMaxSeconds is { } seconds )
        {
            properties.Add( "RuntimeMaxSec=" + seconds.ToString( CultureInfo.InvariantCulture ) );
        }

        return [.. properties];
    }

    public void Verify( string root )
    {
        if ( AreResourceLimitsUnavailable( root ) )
        {
            throw new PlatformNotSupportedException( "The extension resource controllers could not enforce the required limits." );
        }
    }

    private string FormatMemoryLimit( )
    {
        if ( MemoryBytes >= 1L << 30 )
        {
            return (MemoryBytes >> 30).ToString( CultureInfo.InvariantCulture ) + "G";
        }
        else
        {
            return (MemoryBytes >> 20).ToString( CultureInfo.InvariantCulture ) + "M";
        }
    }

    private bool AreResourceLimitsUnavailable( string root )
    {
        return File
                   .ReadAllText( Path.Combine( root, "memory.max" ) )
                   .Trim( ) !=
               MemoryBytes.ToString( CultureInfo.InvariantCulture ) ||
               File
                   .ReadAllText( Path.Combine( root, "pids.max" ) )
                   .Trim( ) !=
               Tasks.ToString( CultureInfo.InvariantCulture ) ||
               File
                   .ReadAllText( Path.Combine( root, "cpu.max" ) )
                   .StartsWith( "max", StringComparison.Ordinal ) ||
               File
                   .ReadAllText( Path.Combine( root, "memory.swap.max" ) )
                   .Trim( ) !=
               "0";
    }
}

internal static class ExtensionSyscallFilter
{
    public static string Deny( bool allowDebug )
    {
        if ( allowDebug )
        {
            return "~@module @raw-io @reboot @swap @obsolete @cpu-emulation";
        }
        else
        {
            return "~@debug @module @raw-io @reboot @swap @obsolete @cpu-emulation";
        }
    }
}

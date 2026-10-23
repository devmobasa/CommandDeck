using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void ExtensionNativeAndAddressPolicyMatchReference( )
    {
        Equal( 24, Unsafe.SizeOf<LinuxOpenHow>( ) );
        Equal( 0x10000, LinuxConstants.DirectoryFlag( Architecture.X64 ) );
        Equal( 0x4000, LinuxConstants.DirectoryFlag( Architecture.Arm64 ) );
        Equal( 0x20000, LinuxConstants.NoFollowFlag( Architecture.X64 ) );
        Equal( 0x8000, LinuxConstants.NoFollowFlag( Architecture.Arm64 ) );
        var random = new Random( 61723 );
        for ( var index = 0; index < 100000; index++ )
        {
            var v4 = new byte[4];
            var v6 = new byte[16];
            random.NextBytes( v4 );
            random.NextBytes( v6 );
            Check( new( v4 ) );
            Check( new( v6 ) );
            Check( new IPAddress( v4 ).MapToIPv6( ) );
        }

        foreach ( var cidr in new[]
                 {
                     "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/16", "192.88.99.0/24",
                     "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/3", "2000::/3", "2001:db8::/32", "2001::/24",
                     "2002::/16", "fc00::/7", "::1/128"
                 } )
        {
            var network = IPNetwork.Parse( cidr );
            var first = network.BaseAddress.GetAddressBytes( );
            var last = first.ToArray( );
            for ( var bit = network.PrefixLength; bit < last.Length * 8; bit++ )
            {
                last[bit / 8] |= ( byte ) (1 << (7 - bit % 8));
            }

            Check( new( first ) );
            Check( new( last ) );
            var before = first.ToArray( );
            var after = last.ToArray( );

            for ( var index = before.Length - 1; index >= 0; index-- )
            {
                if ( before[index]-- != 0 )
                {
                    break;
                }
            }

            for ( var index = after.Length - 1; index >= 0; index-- )
            {
                if ( after[index]++ != 255 )
                {
                    break;
                }
            }

            Check( new( before ) );
            Check( new( after ) );
        }

        static void Check( IPAddress address )
        {
            Equal( ReferencePublicAddress( address ), ExtensionAddressPolicy.PublicAddress( address ) );
            Equal( ReferencePrivateAddress( address ), ExtensionAddressPolicy.PrivateAddress( address ) );
        }
    }

    // Frozen reference from the clean e1a46ad snapshot. Changes to network policy are separate work.
    internal static bool ReferencePublicAddress( IPAddress address )
    {
        if ( address.IsIPv4MappedToIPv6 )
        {
            return false;
        }

        var bytes = address.GetAddressBytes( );
        if ( bytes.Length == 16 )
        {
            return ReferencePublicV6( bytes );
        }

        return PublicV4( bytes );
    }

    private static bool ReferencePublicV6( byte[] bytes )
    {
        if ( (bytes[0] & 0xe0) != 0x20 )
        {
            return false;
        }

        if ( IsDocumentationV6( bytes ) )
        {
            return false;
        }

        return bytes is not ([0x20, 0x01, 0, ..] or [0x20, 0x02, ..]);
    }

    private static bool IsDocumentationV6( byte[] bytes )
    {
        var documentation = IPAddress
            .Parse( "2001:db8::" )
            .GetAddressBytes( );

        return documentation
            .AsSpan( 0, 4 )
            .SequenceEqual( bytes.AsSpan( 0, 4 ) );
    }

    private static bool PublicV4( byte[] bytes )
    {
        if ( bytes[0] is 0 or 10 or 127 or >= 224 )
        {
            return false;
        }

        return !IsReservedV4( bytes );
    }

    private static bool IsReservedV4( byte[] bytes ) => bytes is [100, >= 64 and <= 127, ..]
        or [169, 254, ..]
        or [172, >= 16 and <= 31, ..]
        or [192, 0 or 168, ..]
        or [192, 88, 99, ..]
        or [198, 18 or 19, ..]
        or [198, 51, 100, ..]
        or [203, 0, 113, ..];

    private static bool ReferencePrivateAddress( IPAddress address )
    {
        if ( address.IsIPv4MappedToIPv6 )
        {
            return false;
        }

        if ( IPAddress.IsLoopback( address ) )
        {
            return true;
        }

        var bytes = address.GetAddressBytes( );
        if ( bytes.Length == 16 )
        {
            return (bytes[0] & 0xfe) == 0xfc;
        }

        return bytes is [10, ..] or [172, >= 16 and <= 31, ..] or [192, 168, ..];
    }
}

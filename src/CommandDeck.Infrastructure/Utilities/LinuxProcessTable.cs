using System.Globalization;
using System.Text;

namespace CommandDeck;

// One process as /proc reports it. StartTicks is field 22 of /proc/<pid>/stat,
// the start time in clock ticks after boot; with the PID it identifies one
// process for its whole life, because a reused PID gets a later start time.
public sealed record ProcessEntry(
    int Pid,
    string Name,
    char State,
    ulong StartTicks,
    uint RealUid,
    uint EffectiveUid,
    uint SavedUid,
    uint Flags,
    ulong CpuTicks,
    long ResidentBytes )
{
    // PF_KTHREAD from include/linux/sched.h.
    private const uint KernelThreadFlag = 0x00200000;

    public bool IsKernelThread => (Flags & KernelThreadFlag) != 0;

    public bool IsExited => State is 'Z' or 'X';

    public bool IsOwnedBy( uint userId ) => RealUid == userId && EffectiveUid == userId && SavedUid == userId;
}

// Reads the processes of one user from a proc file system. Every file is read
// with a size bound, and an entry that cannot be read or parsed is skipped.
public sealed class LinuxProcessTable
{
    private const int MinimumStatFields = 20;
    private const int FlagsFieldIndex = 6;
    private const int UserTicksFieldIndex = 11;
    private const int SystemTicksFieldIndex = 12;
    private const int StartTicksFieldIndex = 19;

    private const int MaximumFileBytes = 16 * ByteUnits.BytesPerKibibyte;
    private const int MaximumNameCharacters = 64;

    private readonly string _root;

    public LinuxProcessTable( string root = "/proc", uint? userId = null, int? selfPid = null, string? selfExecutable = null )
    {
        _root = root;
        UserId = userId ?? LinuxProcessSignals.CurrentUserId( );
        SelfPid = selfPid ?? Environment.ProcessId;
        SelfExecutable = selfExecutable ?? DefaultSelfExecutable( );
    }

    public uint UserId { get; }

    public int SelfPid { get; }

    // The daemon's executable; its bridge and helpers share it and are hidden too.
    // Null under the dotnet host, whose path other programs share.
    public string? SelfExecutable { get; }

    // The user's own live processes other than CommandDeck, from at most
    // `maximum` process directories. A failing scan keeps what it has read.
    public IReadOnlyList<ProcessEntry> List( int maximum, CancellationToken cancellationToken )
    {
        List<ProcessEntry> entries = [];
        var scanned = 0;

        try
        {
            foreach ( var directory in Directory.EnumerateDirectories( _root ) )
            {
                cancellationToken.ThrowIfCancellationRequested( );

                if ( !int.TryParse( Path.GetFileName( directory ), NumberStyles.None, CultureInfo.InvariantCulture, out var pid ) )
                {
                    continue;
                }

                if ( ++scanned > maximum )
                {
                    break;
                }

                if ( Read( pid ) is { } entry && IsListable( entry ) )
                {
                    entries.Add( entry );
                }
            }
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            // The proc file system cannot be read further; the processes read so far stand.
        }

        return entries;
    }

    public bool IsListable( ProcessEntry entry ) => entry.Pid != SelfPid &&
                                                    entry is { IsKernelThread: false, IsExited: false } &&
                                                    entry.IsOwnedBy( UserId ) &&
                                                    (SelfExecutable is null ||
                                                     !string.Equals( Executable( entry.Pid ), SelfExecutable, StringComparison.Ordinal ));

    // The current state of one PID, or null when no readable process holds it.
    public ProcessEntry? Read( int pid )
    {
        var directory = Path.Combine( _root, pid.ToString( CultureInfo.InvariantCulture ) );
        var status = ReadSmall( Path.Combine( directory, "status" ) );
        var stat = ReadSmall( Path.Combine( directory, "stat" ) );
        if ( HasMissingProcessFiles( status, stat ) )
        {
            return null;
        }

        var uids = StatusField( status, "Uid:" );
        var resident = StatusField( status, "VmRSS:" );
        var open = stat.IndexOf( '(' );
        var close = stat.LastIndexOf( ')' );
        if ( HasInvalidProcessHeader( uids, open, close, stat ) )
        {
            return null;
        }

        // Fields after the command name start at field 3, the state.
        var fields = stat[(close + 2)..]
            .Split( ' ', StringSplitOptions.RemoveEmptyEntries );

        if ( fields.Length < MinimumStatFields ||
             fields[0].Length != 1 ||
             !TryUnsigned( stat
                 .AsSpan( 0, open )
                 .Trim( ), out var statPid ) ||
             statPid != ( ulong ) pid ||
             !TryUnsigned( fields[FlagsFieldIndex], out var flags ) ||
             !TryUnsigned( fields[UserTicksFieldIndex], out var userTicks ) ||
             !TryUnsigned( fields[SystemTicksFieldIndex], out var systemTicks ) ||
             !TryUnsigned( fields[StartTicksFieldIndex], out var startTicks ) ||
             !TryUnsigned( uids[0], out var realUid ) ||
             !TryUnsigned( uids[1], out var effectiveUid ) ||
             !TryUnsigned( uids[2], out var savedUid ) )
        {
            return null;
        }

        var kilobytes = resident is { Length: >= 1 } && TryUnsigned( resident[0], out var value ) ? ( long ) value : 0;

        return new ProcessEntry( pid, Sanitize( stat[(open + 1)..close] ), fields[0][0], startTicks, ( uint ) realUid, ( uint ) effectiveUid,
            ( uint ) savedUid, ( uint ) flags, userTicks + systemTicks, kilobytes * ByteUnits.BytesPerKibibyte );
    }

    private static bool HasMissingProcessFiles( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? status,
        [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? stat )
    {
        return status is null || stat is null;
    }

    private static bool HasInvalidProcessHeader( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string[]? uids, int open, int close, string stat )
    {
        return uids is not { Length: >= 3 } || open < 0 || close < open || close + 2 > stat.Length;
    }

    // The resolved executable path, or null for a process whose link cannot be read.
    public string? Executable( int pid )
    {
        try
        {
            var target = new FileInfo( Path.Combine( _root, pid.ToString( CultureInfo.InvariantCulture ), "exe" ) ).LinkTarget;

            return target is null ? null : Sanitize( target );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            return null;
        }
    }

    // Control characters in a process name cannot reach a row or a confirmation.
    internal static string Sanitize( string value )
    {
        StringBuilder text = new( Math.Min( value.Length, MaximumNameCharacters ) );

        foreach ( var character in value )
        {
            if ( text.Length == MaximumNameCharacters )
            {
                break;
            }

            text.Append( char.IsControl( character ) ? '?' : character );
        }

        return text.ToString( );
    }

    private static string[]? StatusField( string status, string name )
    {
        foreach ( var line in status.Split( '\n' ) )
        {
            if ( line.StartsWith( name, StringComparison.Ordinal ) )
            {
                return line[name.Length..]
                    .Split( ['\t', ' '], StringSplitOptions.RemoveEmptyEntries );
            }
        }

        return null;
    }

    private static bool TryUnsigned( ReadOnlySpan<char> text, out ulong value ) =>
        ulong.TryParse( text, NumberStyles.None, CultureInfo.InvariantCulture, out value );

    private static string? ReadSmall( string path )
    {
        try
        {
            using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.None );
            var buffer = new byte[MaximumFileBytes];
            var total = 0;
            int read;
            while ( total < buffer.Length && (read = stream.Read( buffer, total, buffer.Length - total )) > 0 )
            {
                total += read;
            }

            return Encoding.UTF8.GetString( buffer, 0, total );
        }
        catch ( Exception exception ) when ( exception is IOException or UnauthorizedAccessException )
        {
            return null;
        }
    }

    private static string? DefaultSelfExecutable( )
    {
        var path = Environment.ProcessPath;

        return path is null || Path.GetFileNameWithoutExtension( path ) == DotnetHost.ExecutableName ? null : path;
    }
}

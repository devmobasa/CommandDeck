namespace CommandDeck;

public static class QuicklinkIdentity
{
    public const string UserQuicklinkPrefix = "user-";
}

public static class QuicklinkStoreActions
{
    public const string Save = "save";
    public const string Remove = "remove";
    public const string Disable = "disable";
    public const string Enable = "enable";
}

/// <summary>Where a quicklink comes from; stored with each user quicklink.</summary>
public static class QuicklinkSources
{
    public const string Configured = "configured";
    public const string User = "user";
}

public sealed class QuicklinkEntry
{
    public required QuicklinkOptions Quicklink { get; init; }
    public required string Source { get; init; }
    public bool Enabled { get; init; } = true;
    public long CreatedUnixMs { get; init; }
}

public sealed class QuicklinksResult
{
    public List<QuicklinkEntry> Quicklinks { get; init; } = [];
    public string? WebFallbackQuicklinkId { get; init; }
    public string? Message { get; init; }
}

public sealed class QuicklinkState
{
    public List<QuicklinkEntry> UserQuicklinks { get; init; } = [];
    public List<string> DisabledConfiguredIds { get; init; } = [];
}

public interface IQuicklinkStore
{
    ValueTask<QuicklinksResult> RestoreQuicklinksAsync( QuicklinkState state, string? fallbackId, CancellationToken cancellationToken );
    ValueTask<QuicklinksResult> GetQuicklinksAsync( CancellationToken cancellationToken );
    ValueTask<QuicklinksResult> ChangeQuicklinkAsync( string action, QuicklinkOptions? quicklink, string? id, CancellationToken cancellationToken );
}

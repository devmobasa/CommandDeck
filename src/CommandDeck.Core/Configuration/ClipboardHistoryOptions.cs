namespace CommandDeck;

public sealed class ClipboardHistoryOptions
{
    public int MaximumEntries { get; init; } = OptionLimits.ClipboardHistory.MaximumEntries.Default;
    public int RetentionHours { get; init; } = OptionLimits.ClipboardHistory.RetentionHours.Default;
}

namespace CommandDeck;

public static partial class OptionLimits
{
    public static class Indexing
    {
        public static class MaximumTextFileBytes
        {
            public const long Default = 2 * ByteUnits.BytesPerMebibyte;
        }

        public static class MaximumDocumentFileBytes
        {
            public const long Default = 20 * ByteUnits.BytesPerMebibyte;
        }

        public static class MaximumExtractedBytes
        {
            public const int Default = 2 * ByteUnits.BytesPerMebibyte;
            public const int Minimum = 1;
            public const int Maximum = 64 * ByteUnits.BytesPerMebibyte;
        }

        public static class IndexDesktopApplications
        {
            public const bool Default = true;
        }

        public static class IndexOmarchyCommands
        {
            public const bool Default = true;
        }

        public static class EnablePdfExtraction
        {
            public const bool Default = false;
        }

        public static class EnablePandocExtraction
        {
            public const bool Default = false;
        }

        public static class ExtractorTimeoutMilliseconds
        {
            public const int Default = 8_000;
            public const int Minimum = 50;
            public const int Maximum = 120_000;
        }

        public static class ExtractorMemoryLimitBytes
        {
            public const long Default = 256L * ByteUnits.BytesPerMebibyte;
            public const long Minimum = 16 * ByteUnits.BytesPerMebibyte;
            public const long Maximum = 4L * ByteUnits.BytesPerGibibyte;
        }

        public static class ExtractorStandardErrorBytes
        {
            public const int Default = 32 * ByteUnits.BytesPerKibibyte;
            public const int Minimum = 1;
            public const int Maximum = ByteUnits.BytesPerMebibyte;
        }

        public static class MaximumConcurrentExtractors
        {
            public const int Default = 2;
            public const int Minimum = 1;
            public const int Maximum = 16;
        }

        public static class EnableIncrementalIndexing
        {
            public const bool Default = true;
        }

        public static class IncrementalDebounceMilliseconds
        {
            public const int Default = 120;
        }

        public static class IncrementalMaximumLatencyMilliseconds
        {
            public const int Default = 750;
        }

        public static class IncrementalEventBatchLimit
        {
            public const int Default = 2_048;
        }

        public static class EnableScheduledMaintenance
        {
            public const bool Default = true;
        }

        public static class MaintenanceIntervalMinutes
        {
            public const int Default = 360;
            public const int Minimum = 1;
            public const int Maximum = 7 * 24 * 60;
        }
    }

    public static class IndexRoot
    {
        public static class IncludeHidden
        {
            public const bool Default = false;
        }

        public static class IncludeFileNameInSearch
        {
            public const bool Default = true;
        }

        public static class IncludeMetadataInSearch
        {
            public const bool Default = true;
        }

        public static class IncludeContentInSearch
        {
            public const bool Default = true;
        }

        public static class FollowSymbolicLinks
        {
            public const bool Default = false;
        }

        public static class ExcludeGeneratedDirectories
        {
            public const bool Default = true;
        }

        public static class RespectGitIgnore
        {
            public const bool Default = true;
        }
    }

    public static class Privacy
    {
        public static class StoreQueryHistory
        {
            public const bool Default = false;
        }

        public static class QueryHistoryRetentionDays
        {
            public const int Default = 90;
            public const int Minimum = 1;
            public const int Maximum = 3650;
        }

        public static class MaximumQueryHistoryEntries
        {
            public const int Default = 1_000;
            public const int Minimum = 1;
            public const int Maximum = 100_000;
        }

        public static class StoreUsageHistory
        {
            public const bool Default = true;
        }

        public static class PersistClipboard
        {
            public const bool Default = false;
        }

        public static class NeverIndex
        {
            public static readonly System.Collections.Immutable.ImmutableArray<string> Default =
            [
                "~/.ssh",
                "~/.gnupg",
                "~/.password-store",
                "~/.local/share/keyrings",
                "~/.local/share/kwalletd",
                "**/.env",
                "**/.env.*",
                "**/*credentials*",
                "**/*secret*",
                "**/*.pem",
                "**/*.key",
                "**/*.p12",
                "**/*.pfx",
                "**/Cookies",
                "**/Login Data"
            ];
        }
    }

    public static class Ui
    {
        public static class PreferredBinding
        {
            public const string Default = "ALT+SPACE";
        }

        public static class AlsoBindSuperSpace
        {
            public const bool Default = false;
        }

        public static class MonitorPolicy
        {
            public const string Default = "focused";
        }

        public static class DebounceMilliseconds
        {
            public const int Default = 45;
        }

        public static class Width
        {
            public const int Default = 760;
        }

        public static class MaximumVisibleRows
        {
            public const int Default = 9;
        }
    }
}

namespace CommandDeck;

/// <summary>Configuration defaults and supported bounds.</summary>
public static partial class OptionLimits
{
    public static class Storage
    {
        public static class DatabasePath
        {
            public const string Default = "$XDG_STATE_HOME/commanddeck/commanddeck.db";
        }

        public static class BusyTimeoutMilliseconds
        {
            public const int Default = 5_000;
        }
    }

    public static class Search
    {
        public static class DefaultLimit
        {
            public const int Default = 50;
        }

        public static class MaximumLimit
        {
            public const int Default = 200;
        }

        public static class ProviderTimeoutMilliseconds
        {
            public const int Default = 350;
        }

        public static class LearnFromInvocations
        {
            public const bool Default = true;
        }

        public static class OmarchyMenuState
        {
            public const bool Default = true;
        }
    }

    public static class Calculator
    {
        public static class Inline
        {
            public const bool Default = true;
        }

        public static class ExchangeRates
        {
            public const bool Default = true;
        }
    }

    public static class Ranking
    {
        public const double MinimumWeight = 0.01;

        public const int MaximumWeight = 100;

        public static class SourceWeights
        {
            public const double Minimum = 0.1;
            public const int Maximum = 5;
        }

        public static class TitleWeight
        {
            public const double Default = 7;
        }

        public static class PathWeight
        {
            public const double Default = 1.5;
        }

        public static class MetadataWeight
        {
            public const double Default = 2;
        }

        public static class ContentWeight
        {
            public const double Default = 1;
        }

        public static class ConfigWeight
        {
            public const double Default = 4;
        }

        public static class MaximumResultsPerSource
        {
            public const int Default = 50;
            public const int Minimum = 1;
            public const int Maximum = 500;
        }

        public static class FirstPageSize
        {
            public const int Default = 9;
            public const int Minimum = 1;
            public const int Maximum = 200;
        }

        public static class MinimumSourcesOnFirstPage
        {
            public const int Default = 2;
            public const int Minimum = 1;
        }
    }

    public static class TopMatches
    {
        public static class Enabled
        {
            public const bool Default = true;
        }
    }

    public static class TimeAwareRanking
    {
        public const int MinimumMultiplier = 0;

        public const int MaximumMultiplier = 5;

        public static class Enabled
        {
            public const bool Default = true;
        }

        public static class ApplicationMultiplier
        {
            public const double Default = 1;
        }

        public static class FileMultiplier
        {
            public const double Default = 0.9;
        }

        public static class WorkspaceMultiplier
        {
            public const double Default = 1.1;
        }

        public static class ActionMultiplier
        {
            public const double Default = 0.8;
        }
    }

    public static class ContextRanking
    {
        public const int MinimumBoost = 0;

        public const double MaximumBoost = 0.5;

        public static class Enabled
        {
            public const bool Default = true;
        }

        public static class ActiveApplicationBoost
        {
            public const double Default = 0.05;
        }

        public static class ActiveWorkspaceBoost
        {
            public const double Default = 0.05;
        }

        public static class CurrentDirectoryBoost
        {
            public const double Default = 0.04;
        }

        public static class SelectedProjectBoost
        {
            public const double Default = 0.06;
        }
    }

    public static class AdaptiveRanking
    {
        public static class Enabled
        {
            public const bool Default = true;
        }

        public static class RetentionDays
        {
            public const int Default = 730;
            public const int Minimum = 1;
            public const int Maximum = 3650;
        }

        public static class MaximumEvents
        {
            public const int Default = 10_000;
            public const int Minimum = 1;
            public const int Maximum = 1_000_000;
        }
    }
}

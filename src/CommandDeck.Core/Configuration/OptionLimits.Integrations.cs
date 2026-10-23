namespace CommandDeck;

public static partial class OptionLimits
{
    public static class WebEngine
    {
        public static class IncludeInGlobal
        {
            public const bool Default = true;
        }

        public static class Name
        {
            public const int Minimum = 1;
            public const int Maximum = 64;
        }

        public static class UrlTemplate
        {
            public const int Minimum = 1;
            public const int Maximum = 2048;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage( "Naming", "CA1716:Identifiers should not match keywords",
            Justification = "Names the Alias configuration option." )]
        public static class Alias
        {
            public const int Minimum = 1;
            public const int Maximum = 32;
        }
    }

    public static class CustomCommand
    {
        public static class RequiresConfirmation
        {
            public const bool Default = false;
        }
    }

    public static class Ai
    {
        public static class Enabled
        {
            public const bool Default = false;
        }

        public static class Providers
        {
            public const int Maximum = 8;
        }
    }

    public static class AiProvider
    {
        public static class AllowRemoteEndpoint
        {
            public const bool Default = false;
        }

        public static class Enabled
        {
            public const bool Default = true;
        }

        public static class ContextLimit
        {
            public const int Minimum = 1;
            public const int Maximum = 10_000_000;
        }

        public static class DisplayName
        {
            public const int MaximumBytes = 96;
        }

        public static class Model
        {
            public const int MaximumBytes = AiLimits.MaximumModelIdBytes;
        }

        public static class BaseUrl
        {
            public const int Maximum = 2048;
        }
    }

    public static class AiCredential
    {
        public const string OpenAiApiKeyEnvironmentVariable = "OPENAI_API_KEY";

        public static class Kind
        {
            public const string Default = AiCredentialKinds.None;
        }
    }

    public static class Agent
    {
        public const int MinimumVersionPart = 0;

        public static class BubblewrapPath
        {
            public const string Default = "/usr/bin/bwrap";
        }

        public static class SystemdRunPath
        {
            public const string Default = "/usr/bin/systemd-run";
        }

        public static class RuntimeDirectory
        {
            public const string Default = "$XDG_RUNTIME_DIR/commanddeck";
        }

        public static class StateDirectory
        {
            public const string Default = "$XDG_STATE_HOME/commanddeck";
        }

        public static class MinimumCodexMajorVersion
        {
            public const int Default = 0;
        }

        public static class MinimumCodexMinorVersion
        {
            public const int Default = 152;
        }

        public static class MinimumCodexPatchVersion
        {
            public const int Default = 1;
        }

        public static class MaximumCodexMajorVersion
        {
            public const int Default = 0;
        }

        public static class MaximumCodexMinorVersion
        {
            public const int Default = 999;
        }

        public static class MaximumCodexPatchVersion
        {
            public const int Default = 999;
        }

        public static class AutoApproveReadOnlyCommands
        {
            public const bool Default = false;
        }
    }

    public static class FileTypeIndexPolicy
    {
        public const int MinimumLimit = 1;
    }

    public static class ClipboardHistory
    {
        public static class MaximumEntries
        {
            public const int Default = 200;
            public const int Minimum = 10;
            public const int Maximum = 1000;
        }

        public static class RetentionHours
        {
            public const int Default = 168;
            public const int Minimum = 1;
            public const int Maximum = 8760;
        }

        public static class QuotaMiB
        {
            public const int Minimum = 64;
            public const int Maximum = 1024;
        }
    }

    public static class Project
    {
        public static class Editor
        {
            public const string Default = "rider";
        }

        public static class EditorArguments
        {
            public static readonly System.Collections.Immutable.ImmutableArray<string> Default = [EditorArgumentTokens.Path];
        }
    }

    public static class Identifiers
    {
        public const int MinimumCharacters = 1;

        public const int MaximumCharacters = 64;
    }

    public static class CommandDeck
    {
        public static class Projects
        {
            public const int Maximum = 100;
        }

        public static class ScriptDirectories
        {
            public static readonly System.Collections.Immutable.ImmutableArray<string> Default = ["$XDG_CONFIG_HOME/commanddeck/scripts"];
            public const int Maximum = 16;
        }
    }
}

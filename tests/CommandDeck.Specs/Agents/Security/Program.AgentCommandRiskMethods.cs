namespace CommandDeck.Specs;

public static partial class Program
{
    private static void ClassifyCommandRiskAllowsOnlyDirectReadOnlyCommands( )
    {
        static AgentCommandPlan Plan( string executable, params string[] argv ) => new( executable, "identity", [executable, .. argv], "/home/user",
            AgentCommandTool.MinimalProfile, new Dictionary<string, string>( StringComparer.Ordinal ), "digest" );

        // Direct invocations of known read-only tools auto-approve.
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/ls", "-la" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/cat", "/etc/hostname" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/grep", "needle", "file" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/grep", "-o", "needle" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/sort", "names.txt" ) ) );

        // A shell is never auto-approved, even when its script only reads state:
        // the script cannot be classified, so it always confirms.
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/bash", "-lc", "ls -la" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/bin/sh", "-c", "cat secrets" ) ) );

        // Writers, package managers (dedicated read-only tool exists), interpreters,
        // and anything unlisted stay confirmed.
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/rm", "-rf", "x" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/pacman", "-Ss", "firefox" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/yay", "-Ss", "ripgrep" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/env", "FOO=1", "cmd" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/find", ".", "-delete" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( string.Empty, "x" ) ) );

        // A write flag on an otherwise read-only tool still confirms.
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/sort", "-o", "/tmp/out" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/sort", "--output=/tmp/out" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/sort", "-uo", "/tmp/out" ) ) );

        // Basename matching is not identity: a temp script named ls still confirms.
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( Path.Combine( Path.GetTempPath( ), "ls" ), "-la" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/../tmp/ls", "-la" ) ) );

        // Unclassified argument forms confirm, including write operands and
        // unique-prefix long options GNU accepts as abbreviations.
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/uniq", "in.txt", "out.txt" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", "-C", "-m", "magic" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/sort", "--out", "/tmp/out", "names.txt" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/uniq", "in.txt" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", "-m", "magic", "sample.bin" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/sort", "--reverse", "names.txt" ) ) );

        // A required option value can itself be "--". It must not hide later
        // flags from the classifier when the executable still parses them.
        foreach ( var option in new[] { "-F", "--separator", "-m", "--magic-file" } )
        {
            Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", option, "--", "-C", "-m", "magic" ) ) );
            Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", option, "--", "sample.bin" ) ) );
            Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", option ) ) );
        }

        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", "-bF--", "-C", "-m", "magic" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", "--separator=--", "-C", "-m", "magic" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", "-F", "--", "--", "-C" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", "-mcustom.magic", "sample.bin" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/uniq", "-f", "1", "in.txt" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/uniq", "--skip-fields", "1", "in.txt", "out.txt" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/date", "090400002026" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/date", "--", "090400002026" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/date", "--date", "2026-09-04", "+%F" ) ) );
        Equal( "immediate", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/date", "+%F" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", "-z", "archive.lz4" ) ) );
        Equal( "confirm", AgentLauncherTools.ClassifyCommandRisk( Plan( "/usr/bin/file", "--uncompress", "archive.lz4" ) ) );
    }
}

namespace CommandDeck;

internal static class SystemdCommandNames
{
    internal const string Control = "systemctl";
    internal const string Run = "systemd-run";
    internal const string ControlPath = "/usr/bin/" + Control;
    internal const string RunPath = "/usr/bin/" + Run;
}

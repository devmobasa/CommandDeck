namespace CommandDeck;

public static class DaemonChildProcessContract
{
    public const string ClipboardHost = "clipboard-host";
    public const string ExtensionWorker = "extension-worker";

    public const string Capture = "--capture";
    public const string ControlRead = "--control-read";
    public const string ControlWrite = "--control-write";
    public const string HostEpoch = "--host-epoch";
    public const string DebugSocket = "--debug-socket";
    public const string Artifact = "--artifact";
    public const string Entrypoint = "--entrypoint";
    public const string OwnerPid = "--owner-pid";
    public const string OwnerStart = "--owner-start";
}

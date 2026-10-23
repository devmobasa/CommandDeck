namespace CommandDeck;

public static class ExtensionProbeFields
{
    public const string HostFile = "hostFile";
    public const string HostSocket = "hostSocket";
    public const string HostHome = "hostHome";
    public const string Port = "port";
    public const string FileDenied = "fileDenied";
    public const string HomeDenied = "homeDenied";
    public const string SocketDenied = "socketDenied";
    public const string NetworkDenied = "networkDenied";
    public const string PackageWriteDenied = "packageWriteDenied";
    public const string TemporaryWorks = "temporaryWorks";
    public const string EnvironmentClean = "environmentClean";
    public const string PtraceDenied = "ptraceDenied";

    public static readonly System.Collections.Immutable.ImmutableArray<string> RequiredReportFields =
    [
        FileDenied, HomeDenied, SocketDenied, NetworkDenied, PackageWriteDenied, TemporaryWorks, EnvironmentClean, PtraceDenied
    ];
}

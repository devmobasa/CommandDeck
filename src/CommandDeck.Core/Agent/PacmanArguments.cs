namespace CommandDeck;

public static class PacmanArguments
{
    public const string InstallDigestPrintFormat = "%n|%v|%r";
    public const string InstallPreviewPrintFormat = "%n|%v|%r|%s|%l";

    public static IReadOnlyList<string> CommitFlags { get; } = ["--noconfirm", "--noprogressbar", "--color", "never"];

    public static List<string> BuildInstallPrintArguments( IReadOnlyList<string> names, string format ) =>
        ["-S", "--print", "--print-format", format, "--needed", "--", .. names];

    public static List<string> BuildRemovePrintArguments( IReadOnlyList<string> names ) =>
        ["-Rs", "--print", "--print-format", "%n|%v", "--", .. names];

    public static string CommitMode( string operation ) => operation switch
    {
        PackageOperations.Install => "-S",
        PackageOperations.Remove => "-Rns",
        _ => throw new ArgumentOutOfRangeException( nameof( operation ) )
    };
}

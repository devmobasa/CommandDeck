namespace CommandDeck.ExtensionProtocol;

public static class ExtensionPackageFiles
{
    public const string Manifest = "commanddeck.extension.json";
    public const string AssetsDirectory = "assets";
    public const string AssetPrefix = AssetsDirectory + "/";
    public const string DevelopmentIdSuffix = ".development";

    public static string BinaryDirectory( string target ) => "bin/" + target;

    public static string Entrypoint( string target, string relativePath ) => BinaryDirectory( target ) + "/" + relativePath;
}

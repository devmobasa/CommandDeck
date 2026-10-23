using System.Text.Json;
using System.Text.Json.Nodes;
using CommandDeck.ExtensionProtocol;

namespace CommandDeck.ExtensionTools;

internal static class DevelopmentArtifact
{
    private const string ArtifactDirectoryPrefix = "artifact-";
    private const string DevelopmentVersionSuffix = "+dev.";

    public static string CreateManifest( string directory, string publish )
    {
        var stage = Path.Combine( Path.GetDirectoryName( publish )!, ArtifactDirectoryPrefix +
                                                                     Guid
                                                                         .NewGuid( )
                                                                         .ToString( "N" ) );

        Directory.CreateDirectory( stage );

        var manifest = JsonNode.Parse( File.ReadAllText( Path.Combine( directory, CommandDeck.ExtensionProtocol.ExtensionPackageFiles.Manifest ) ) )!;
        manifest["id"] = manifest["id"]!.GetValue<string>( ) + ExtensionPackageFiles.DevelopmentIdSuffix;
        manifest["name"] = manifest["name"]!.GetValue<string>( ) + " (Development)";

        manifest["version"] = manifest["version"]!
                                  .GetValue<string>( )
                                  .Split( '+' )[0] +
                              DevelopmentVersionSuffix +
                              Guid
                                  .NewGuid( )
                                  .ToString( "N" );

        foreach ( var command in manifest["commands"]!.AsArray( ) )
        {
            command!["title"] = "Dev: " + command["title"]!.GetValue<string>( );
        }

        var path = Path.Combine( stage, CommandDeck.ExtensionProtocol.ExtensionPackageFiles.Manifest );

        File.WriteAllText( path, manifest.ToJsonString( new JsonSerializerOptions { WriteIndented = true } ) );

        var assets = ExtensionPackageBuilder.CollectAssets( Path.Combine( directory, ExtensionPackageFiles.AssetsDirectory ) );
        if ( assets.Length > 0 )
        {
            Directory.CreateDirectory( Path.Combine( stage, ExtensionPackageFiles.AssetsDirectory ) );
        }

        foreach ( var asset in assets )
        {
            File.Copy( asset, Path.Combine( stage, ExtensionPackageFiles.AssetsDirectory, Path.GetFileName( asset ) ) );
        }

        return path;
    }
}

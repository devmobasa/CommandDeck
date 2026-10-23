namespace CommandDeck.Specs;

public static partial class Program
{
    private static void OptionalToolCatalogPrintsExactPacmanCommand( )
    {
        Equal( "not found. Install with: sudo pacman -S --needed poppler", OptionalToolCatalog.DoctorDetail( "pdftotext", found: false ) );
        Equal( "found on PATH", OptionalToolCatalog.DoctorDetail( "pdftotext", found: true ) );
        var found = OptionalToolCatalog.TryGet( "pdftotext", out var poppler );
        if ( IsMissingOptionalTool( found, poppler ) )
        {
            throw new InvalidOperationException( "pdftotext must be in the optional tool catalog." );
        }

        Equal( "poppler", poppler.Package );
        Equal( "pdftotext|pandoc|wl-copy|hyprctl|xdg-open|omarchy-shell", string.Join( '|', OptionalToolCatalog.All.Select( tool => tool.Command ) ) );

        static bool IsMissingOptionalTool( bool found, [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] OptionalTool? tool ) => !found || tool is null;
    }

    private static async Task ConfigInitWritesSchemaHintAsync( )
    {
        var directory = SpecTemporaryFiles.PathFor( $"commanddeck-schema-init-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( directory );
        var path = Path.Combine( directory, "commanddeck.jsonc" );

        try
        {
            await ConfigurationLoader.WriteDefaultAsync( path, overwrite: true, CancellationToken.None );
            var json = await File.ReadAllTextAsync( path );
            True( json.Contains( "\"$schema\":\"commanddeck.schema.json\"", StringComparison.Ordinal ) );
            var loaded = await ConfigurationLoader.LoadAsync( path, CancellationToken.None );
            True( loaded.Search.Calculator.UseQalc is null );
            True( loaded.Search.Calculator.ExchangeRates );
        }
        finally
        {
            Directory.Delete( directory, recursive: true );
        }

        var besideBinary = Path.Combine( AppContext.BaseDirectory, $"commanddeck-init-{Guid.NewGuid( ):N}.jsonc" );
        var schemaBesideBinary = Path.Combine( AppContext.BaseDirectory, "commanddeck.schema.json" );
        var plantedSchema = false;

        try
        {
            if ( !File.Exists( schemaBesideBinary ) )
            {
                File.WriteAllText( schemaBesideBinary, "{}\n" );
                plantedSchema = true;
            }

            await ConfigurationLoader.WriteDefaultAsync( besideBinary, overwrite: true, CancellationToken.None );

            True( File.Exists( besideBinary ), "config init must succeed when the config directory is the binary directory" );
        }
        finally
        {
            if ( File.Exists( besideBinary ) )
            {
                File.Delete( besideBinary );
            }

            if ( ShouldDeletePlantedSchema( schemaBesideBinary, plantedSchema ) )
            {
                File.Delete( schemaBesideBinary );
            }

            static bool ShouldDeletePlantedSchema( string schemaBesideBinary, bool plantedSchema )
            {
                return plantedSchema && File.Exists( schemaBesideBinary );
            }
        }

        var aliasRoot = SpecTemporaryFiles.PathFor( $"commanddeck-schema-alias-{Guid.NewGuid( ):N}" );
        Directory.CreateDirectory( aliasRoot );
        var aliasDirectory = Path.Combine( aliasRoot, "alias" );
        var aliasConfig = Path.Combine( aliasDirectory, $"commanddeck-init-{Guid.NewGuid( ):N}.jsonc" );
        var plantedAliasSchema = false;

        try
        {
            if ( !File.Exists( schemaBesideBinary ) )
            {
                File.WriteAllText( schemaBesideBinary, "{}\n" );
                plantedAliasSchema = true;
            }

            Directory.CreateSymbolicLink( aliasDirectory, AppContext.BaseDirectory );
            await ConfigurationLoader.WriteDefaultAsync( aliasConfig, overwrite: true, CancellationToken.None );

            True( File.Exists( aliasConfig ), "config init must succeed when the config directory is a symlink to the binary directory" );
        }
        finally
        {
            if ( File.Exists( aliasConfig ) )
            {
                File.Delete( aliasConfig );
            }

            if ( HasAliasDirectory( aliasDirectory ) )
            {
                Directory.Delete( aliasDirectory );
            }

            Directory.Delete( aliasRoot, recursive: true );

            if ( ShouldDeleteAliasSchema( schemaBesideBinary, plantedAliasSchema ) )
            {
                File.Delete( schemaBesideBinary );
            }

            static bool HasAliasDirectory( string aliasDirectory )
            {
                return Directory.Exists( aliasDirectory ) || File.Exists( aliasDirectory );
            }

            static bool ShouldDeleteAliasSchema( string schemaBesideBinary, bool plantedAliasSchema )
            {
                return plantedAliasSchema && File.Exists( schemaBesideBinary );
            }
        }
    }
}

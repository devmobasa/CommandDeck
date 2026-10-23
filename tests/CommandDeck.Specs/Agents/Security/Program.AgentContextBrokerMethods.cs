namespace CommandDeck.Specs;

public static partial class Program
{
    private static async Task AgentContextBrokerEnforcesLevelsAndProtectedPathsAsync( )
    {
        var root = SpecTemporaryFiles.PathFor( $"commanddeck-agent-ctx-{Guid.NewGuid( ):N}" );
        var home = Path.Combine( root, "home" );
        Directory.CreateDirectory( Path.Combine( home, "notes" ) );
        Directory.CreateDirectory( Path.Combine( home, ".ssh" ) );
        Directory.CreateDirectory( Path.Combine( root, "outside" ) );
        await File.WriteAllTextAsync( Path.Combine( home, "notes", "todo.md" ), "buy milk\nfix bug\n" );
        await File.WriteAllTextAsync( Path.Combine( home, ".ssh", "id_ed25519" ), "secret" );
        await File.WriteAllTextAsync( Path.Combine( home, "notes", "secrets.json" ), "{}" );
        await File.WriteAllBytesAsync( Path.Combine( home, "notes", "binary.bin" ), [1, 0, 2, 0] );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( default );
            AgentContextBroker broker = new( new AgentAuthority( catalog.AgentPreferences ), home );
            AgentPreferences level1 = new( ) { TrustLevel = AgentTrustLevels.Launcher };
            AgentPreferences level2 = new( ) { TrustLevel = AgentTrustLevels.ApprovedContext };
            AgentPreferences level3 = new( ) { TrustLevel = AgentTrustLevels.ReadEntireHome };

            var denied = await broker.AuthorizeAsync( "notes/todo.md", AgentContextOperations.ReadFile, level1, default );

            True( denied is { Allowed: false, RequiresDecision: false } );

            var outside = await broker.AuthorizeAsync( Path.Combine( root, "outside" ), AgentContextOperations.ListDirectory, level3, default );

            True( outside is { Allowed: false, RequiresDecision: false } );

            var protectedFile = await broker.AuthorizeAsync( "~/.ssh/id_ed25519", AgentContextOperations.ReadFile, level3, default );

            True( protectedFile is { Allowed: false, RequiresDecision: true, Protected: true, ProposedFolder: null } );

            AgentPreferences unrestricted = new( ) { TrustLevel = AgentTrustLevels.Unrestricted };

            var protectedUnrestricted = await broker.AuthorizeAsync( "~/.ssh/id_ed25519", AgentContextOperations.ReadFile, unrestricted, default );

            True( protectedUnrestricted is { Allowed: true, RequiresDecision: false, Protected: true },
                "unrestricted trust reads protected paths without a prompt" );

            True( broker.IsProtected( Path.Combine( home, "notes", "secrets.json" ) ) );

            var pending = await broker.AuthorizeAsync( "~/notes/todo.md", AgentContextOperations.ReadFile, level2, default );

            True( pending is { Allowed: false, RequiresDecision: true } && pending.ProposedFolder == Path.Combine( home, "notes" ) );

            var entireHome = await broker.AuthorizeAsync( "notes/todo.md", AgentContextOperations.ReadFile, level3, default );

            True( entireHome.Allowed );
            var grant = await broker.GrantFolderAsync( Path.Combine( home, "notes" ), default );
            Equal( "active", grant.State );

            var granted = await broker.AuthorizeAsync( "~/notes/todo.md", AgentContextOperations.ReadFile, level2, default );

            True( granted.Allowed );

            await ThrowsAsync<InvalidOperationException>( async ( ) => await broker.GrantFolderAsync( Path.Combine( home, ".ssh" ), default ) );

            var entries = broker.ListDirectory( Path.Combine( home, "notes" ), 16 );
            True( entries.Any( entry => entry.Name == "todo.md" ) );
            True( !entries.Any( entry => entry.Name == "secrets.json" ) );
            var homeEntries = broker.ListDirectory( home, 16 );
            True( !homeEntries.Any( entry => entry.Name == ".ssh" ) );
            var text = broker.ReadTextFile( Path.Combine( home, "notes", "todo.md" ), 64, out var truncated );
            True( text.StartsWith( "buy milk", StringComparison.Ordinal ) && !truncated );

            Throws<InvalidOperationException>( ( ) => broker.ReadTextFile( Path.Combine( home, "notes", "binary.bin" ), 64, out _ ) );

            var matches = broker.SearchText( Path.Combine( home, "notes" ), "BUG", 8, 4096, default );
            Equal( 1, matches.Count );
            Equal( 2, matches[0].Line );
            Directory.Move( Path.Combine( home, "notes" ), Path.Combine( home, "notes-moved" ) );
            Directory.CreateDirectory( Path.Combine( home, "notes" ) );
            True( !await broker.TryConfirmGrantAsync( Path.Combine( home, "notes" ), default ) );
            var grants = await catalog.AgentPreferences.GetAgentGrantsAsync( default );
            Equal( "stale", grants[0].State );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }

    private static async Task AgentContextBrokerRejectsIntermediateSymlinkEscapesAsync( )
    {
        if ( !OperatingSystem.IsLinux( ) )
        {
            throw new SpecSkippedException( "This fixture requires Linux." );
        }

        var root = SpecTemporaryFiles.PathFor( $"commanddeck-agent-ctx-link-{Guid.NewGuid( ):N}" );
        var home = Path.Combine( root, "home" );
        var safe = Path.Combine( home, "safe" );
        var protectedDirectory = Path.Combine( home, ".ssh" );
        Directory.CreateDirectory( safe );
        Directory.CreateDirectory( protectedDirectory );
        await File.WriteAllTextAsync( Path.Combine( safe, "note.txt" ), "approved" );
        await File.WriteAllTextAsync( Path.Combine( protectedDirectory, "note.txt" ), "secret" );
        Directory.CreateSymbolicLink( Path.Combine( safe, "redirect" ), protectedDirectory );

        try
        {
            await using SqliteCatalog catalog = new( new StorageOptions { DatabasePath = Path.Combine( root, "catalog.db" ) } );

            await catalog.InitializeAsync( default );
            AgentContextBroker broker = new( new AgentAuthority( catalog.AgentPreferences ), home );
            AgentPreferences preferences = new( ) { TrustLevel = AgentTrustLevels.ReadEntireHome };
            var redirected = Path.Combine( safe, "redirect", "note.txt" );

            var authorized = await broker.AuthorizeAsync( redirected, AgentContextOperations.ReadFile, preferences, default );

            True( authorized.Allowed );

            Throws<InvalidOperationException>( ( ) => broker.ReadTextFile( authorized.CanonicalPath, 64, out _ ) );
            Throws<InvalidOperationException>( ( ) => broker.ListDirectory( Path.Combine( safe, "redirect" ), 8 ) );
            Throws<InvalidOperationException>( ( ) => broker.SearchText( Path.Combine( safe, "redirect" ), "secret", 8, 1024, default ) );

            var beforeSwap = await broker.AuthorizeAsync( Path.Combine( safe, "note.txt" ), AgentContextOperations.ReadFile, preferences, default );

            Directory.Move( safe, Path.Combine( home, "safe-original" ) );
            Directory.CreateSymbolicLink( safe, protectedDirectory );

            Throws<InvalidOperationException>( ( ) => broker.ReadTextFile( beforeSwap.CanonicalPath, 64, out _ ) );
        }
        finally
        {
            Directory.Delete( root, recursive: true );
        }
    }
}

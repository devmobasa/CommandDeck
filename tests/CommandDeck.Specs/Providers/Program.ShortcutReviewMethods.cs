using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void ShortcutReaderRejectsShadowedHelpersAndUncertainRequires( )
    {
        const string call = "o.bind(\"SUPER + Q\", \"Menu\", hl.dsp.exec_cmd(\"menu\"))\n";
        foreach ( var shadow in new[] { "local o = {}", "local hl", "local\no", "local x <const>,\nhl", "o = {}", "hl = other",
             "function o.bind() end", "o.bind = other", "hl.dsp = other", "local x, o = 1, {}" } )
        {
            True( !LuaBindingReader.Read( shadow + "\n" + call ).Bindings[^1].Literal, shadow );
            True( !LuaBindingReader.Read( call + shadow + "\n" ).Bindings[0].Literal, "later assignment: " + shadow );
        }

        foreach ( var source in new[] { "if enabled then require('hypr.bindings') end", "for x = 1, 2 do require('hypr.bindings') end",
                     "function later() require('hypr.bindings') end", "while require('hypr.bindings') do end",
                          "repeat work() until\nrequire('hypr.bindings')",
                          "if enabled then if package.searchpath('hypr.bindings', package.path) then require('hypr.bindings') end end",
                     "if package.searchpath('other', package.path) then require('hypr.bindings') end" } )
        {
            True( !LuaBindingReader.Read( source ).Requires[^1].Ordered, source );
        }

        True( LuaBindingReader.Read( "if package.searchpath(\"hypr.bindings\", package.path) then require(\"hypr.bindings\") end" ).Requires
            .Single( ).Ordered );
        True( LuaBindingReader.Read( "repeat work() until ready\nrequire('hypr.bindings')" ).Requires.Single( ).Ordered );

        foreach ( var source in new[] { "if enabled then require('hypr.bindings') end", "for x = 1, 2 do require('hypr.bindings') end",
                     "do require('hypr.bindings') end" } )
        {
            True( LuaBindingReader.Read( source ).Requires[^1] is { Ordered: false, InPlace: true }, "in place: " + source );
        }

        foreach ( var source in new[] { "function later() require('hypr.bindings') end",
                     "if enabled then local f = function() require('hypr.bindings') end end" } )
        {
            True( !LuaBindingReader.Read( source ).Requires[^1].InPlace, "deferred: " + source );
        }
        True( LuaBindingReader.Read( "-- local o = {}\n" + call ).Bindings.Single( ).Literal );
    }

    private static async Task ShortcutGeneratedExecutionPreservesSharedBindingsAsync( )
    {
        using ShortcutExecutionFixture fixture = new( "o.bind('SUPER + Q', 'Companion', 'companion')\n" );
        var menu = await fixture.RowAsync( "Menu" );
        await fixture.ChangeAsync( menu.Id, "Super+J" );
        fixture.Expect( "Super+Q", "Companion" );
        fixture.Expect( "Super+J", "Menu" );
        await fixture.Service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = menu.Id }, true, fixture.Inventory, default );
        fixture.Expect( "Super+Q", "Menu", "Companion" );
        await fixture.ChangeAsync( menu.Id, "" );
        fixture.Expect( "Super+Q", "Companion" );
        await fixture.Service.ChangeAsync( ProtocolOperations.ShortcutResetAll, new( ) { Id = "all" }, true, fixture.Inventory, default );
        fixture.Expect( "Super+Q", "Menu", "Companion" );
        var terminal = await fixture.RowAsync( "Terminal" );
        await fixture.ChangeAsync( terminal.Id, "Super+Q", replace: true );
        fixture.Expect( "Super+Q", "Terminal", "Companion" );
        await fixture.Service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = terminal.Id }, true, fixture.Inventory, default );
        fixture.Expect( "Super+Q", "Menu", "Companion" );
        await fixture.Service.ChangeAsync( ProtocolOperations.ShortcutHotkey, new( ) { Id = "hotkey", Chord = "Super+Q", Replace = true }, false,
             fixture.Inventory, default );
        fixture.Expect( "Super+Q", "CommandDeck", "Companion" );
        var companion = await fixture.RowAsync( "Companion" );
        await fixture.ChangeAsync( companion.Id, "Super+F12" );
        fixture.Expect( "Super+Q", "CommandDeck" );
        fixture.Expect( "Super+F12", "Companion" );
        await fixture.Service.ChangeAsync( ProtocolOperations.ShortcutResetAll, new( ) { Id = "all" }, true, fixture.Inventory, default );
        fixture.Expect( "Super+Q", "Menu", "Companion" );
    }

    private static async Task ShortcutGeneratedExecutionRejectsUnportableSharedBindingsAsync( )
    {
        foreach ( var extra in new[] {
                     "for n = 1, 1 do o.bind('SUPER + Q', 'Dynamic', handler) end\n",
                     ("hl.define_submap('other', function()\n  hl.bind('SUPER + Q', hl.dsp.exec_cmd('submap'), " +
                         "{description='Submap'})\nend)\n") } )
        {
            using ShortcutExecutionFixture fixture = new( extra );
            var menu = await fixture.RowAsync( "Menu" );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await fixture.ChangeAsync( menu.Id, "Super+J" ) );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await fixture.ChangeAsync( menu.Id, "" ) );
            var terminal = await fixture.RowAsync( "Terminal" );
            await ThrowsAsync<InvalidOperationException>( async ( ) => await fixture.ChangeAsync( terminal.Id, "Super+Q", replace: true ) );
            True( !File.Exists( fixture.Service.ModuleFile ) );
            fixture.Expect( "Super+Q", extra.Contains( "Dynamic", StringComparison.Ordinal ) ? ["Menu", "Dynamic"] : ["Menu"] );
        }
    }

    private static async Task ShortcutGeneratedExecutionOrdersLauncherAndEscapeAsync( )
    {
        using ShortcutExecutionFixture fixture = new( "o.bind('ESCAPE', 'Escape action', 'escape')\n" );
        var menu = await fixture.RowAsync( "Menu" );
        await fixture.ChangeAsync( menu.Id, "Super+T", replace: true );
        fixture.Expect( "Super+T", "Menu" );
        await fixture.Service.ChangeAsync( ProtocolOperations.ShortcutResetAll, new( ) { Id = "all" }, true, fixture.Inventory, default );
        await fixture.Service.ChangeAsync( ProtocolOperations.ShortcutHotkey, new( ) { Id = "hotkey", Chord = "Super+Q", Replace = true }, false,
             fixture.Inventory, default );
        fixture.Expect( "Super+Q", "CommandDeck" );
        await fixture.Service.ChangeAsync( ProtocolOperations.ShortcutRestore, new( ) { Id = "hotkey" }, true, fixture.Inventory, default );
        fixture.Expect( "Super+Q", "Menu" );
        await fixture.ChangeAsync( menu.Id, "Ctrl+Space", replace: true );
        fixture.Expect( "Ctrl+Space", "Menu" );
        var escape = await fixture.RowAsync( "Escape action" );
        await fixture.ChangeAsync( escape.Id, "F12" );
        Equal( "Record cancel", fixture.Table.Names( "Escape", ShortcutLua.RecordingSubmap ) );
        fixture.Table.EnterSubmap( ShortcutLua.RecordingSubmap );
        fixture.Table.PressEscape( );
        Equal( "", fixture.Table.CurrentSubmap );
        True( fixture.Table.Cancelled );
        FakeHyprlandBindings exported = new( );
        exported.Execute( File.ReadAllText( fixture.Files.Paths.Bindings ), "" );
        exported.Execute( HyprlandHotkeyConfig.GenerateFile( HyprlandHotkeyConfig.DefaultChord ), "" );
        exported.Execute( (await fixture.Service.SnapshotAsync( fixture.Inventory, default )).Lua, "" );
        Equal( "Record cancel", exported.Names( "Escape", ShortcutLua.RecordingSubmap ) );
        Equal( "Menu", exported.Names( "Ctrl+Space" ) );
    }

    private static async Task ShortcutSupportLoaderDefinesEscapeAfterKeysAsync( )
    {
        using ShortcutExecutionFixture fixture = new( "o.bind('ESCAPE', 'Escape action', 'escape')\n" );
        File.Delete( fixture.Files.Paths.HotkeyFile );
        File.WriteAllText( fixture.Files.Paths.MainConfig, HyprlandHotkeyConfig.RemoveRequire( File.ReadAllText( fixture.Files.Paths.MainConfig ) ) );
        fixture.Table.Clear( );
        fixture.Table.Execute( File.ReadAllText( fixture.Files.Paths.Bindings ), "" );
        var escape = await fixture.RowAsync( "Escape action" );
        await fixture.ChangeAsync( escape.Id, "F12" );
        Equal( "Record cancel", fixture.Table.Names( "Escape", ShortcutLua.RecordingSubmap ) );
        fixture.Expect( "Ctrl+Space" );
        True( !(await fixture.Service.SnapshotAsync( fixture.Inventory, default )).HotkeyManaged );
    }

    private static void ShortcutGeneratedExecutionSupportsSwaps( )
    {
        FakeHyprlandBindings table = new( );
        table.Execute( "o.bind('SUPER + Q', 'Menu', 'menu')\no.bind('SUPER + T', 'Terminal', 'terminal')\n", "" );
        table.Execute( ShortcutLua.Generate( [
            new( ) { OriginalChord = "Super+Q", Chord = "Super+T", Callee = "o.bind", Description = "Menu", Dispatcher = "'menu'" },
            new( ) { OriginalChord = "Super+T", Chord = "Super+Q", Callee = "o.bind", Description = "Terminal", Dispatcher = "'terminal'" }] ), "" );
        Equal( "Terminal", table.Names( "Super+Q" ) );
        Equal( "Menu", table.Names( "Super+T" ) );
        Throws<InvalidOperationException>( ( ) => ClipboardShortcut.Parse( "Alt+K" ) );
        True( SavedSearchValidator.IsReserved( "ALT+K" ) );
        Equal( "CTRL+SHIFT+K", ClipboardShortcut.Parse( "Ctrl+Shift+K" ) );
    }

    private static async Task ShortcutConcurrentPanelsValidateFreshInventoryAsync( )
    {
        var fixture = new ShortcutExecutionFixture( "" );
        var menu = await fixture.RowAsync( "Menu" );
        var terminal = await fixture.RowAsync( "Terminal" );
        TaskCompletionSource entered = new( TaskCreationOptions.RunContinuationsAsynchronously );
        TaskCompletionSource release = new( TaskCreationOptions.RunContinuationsAsynchronously );
        var reads = 0;
        fixture.BeforeInventory = async ( ) =>
        {
            if ( Interlocked.Increment( ref reads ) == 1 )
            {
                entered.SetResult( );
                await release.Task;
            }
        };
        using CancellationTokenSource stop = new( );
        var (runtime, _, root, _) = await StartAgentRuntimeAsync( new ScriptedAgentBackend( ), stop.Token,
            new RuntimeConstructionOptions { ShortcutEditorFactory = ( ) => fixture.Service } );
        try
        {
            await using ( runtime )
            {
                var first = runtime.ChangeShortcutAsync( new( )
                {
                    Operation = ProtocolOperations.ShortcutSet,
                    Shortcut = new( )
                    {
                        Id = menu.Id,
                        Chord = "Super+F11"
                    }
                }, default ).AsTask( );
                await entered.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );
                var second = runtime.ChangeShortcutAsync( new( )
                {
                    Operation = ProtocolOperations.ShortcutSet,
                    Shortcut = new( ) { Id = terminal.Id, Chord = "Super+F11" }
                }, default ).AsTask( );
                release.SetResult( );
                var results = await Task.WhenAll( first, second );
                True( results[0].ConflictDescription is null );
                Equal( "Menu", results[1].ConflictDescription );
                fixture.Expect( "Super+F11", "Menu" );
                fixture.Expect( "Super+T", "Terminal" );
            }
        }
        finally
        {
            release.TrySetResult( );
            stop.Cancel( );
            Directory.Delete( root, recursive: true );
            fixture.Files.Dispose( );
        }
    }

    // Interprets generated calls in order. Unbind erases every matching key,
    // including other submaps. Binding identities and expectations are independent.
    private sealed class FakeHyprlandBindings
    {
        private readonly List<(string Chord, string Name, string Submap, string Dispatcher)> _bindings = [];

        public string CurrentSubmap { get; private set; } = "";
        public bool Cancelled { get; private set; }

        public void Clear( ) => _bindings.Clear( );

        public void EnterSubmap( string name ) => CurrentSubmap = name;

        public string Names( string chord, string submap = "" ) => string.Join( '|', _bindings.Where( bind => bind.Chord ==
             ShortcutLua.CanonicalChord( chord ) && bind.Submap == submap ).Select( static bind => bind.Name ).Order( StringComparer.Ordinal ) );

        public IReadOnlyList<Keybinding> Inventory => _bindings.Select( static bind => new Keybinding
        {
            Chord = bind.Chord,
            Description = bind.Name,
            Submap = bind.Submap
        } ).ToArray( );
        public string Json => new JsonArray( [.. _bindings.Select( bind => ( JsonNode ) new JsonObject
        {
            ["modmask"] = Mask( bind.Chord ), ["key"] = bind.Chord.Split( '+' )[^1], ["description"] = bind.Name, ["submap"] = bind.Submap
        } )] ).ToJsonString( );

        private static int Mask( string chord ) => chord
            .Split( '+' )
            .SkipLast( 1 )
            .Sum( static modifier => modifier switch
            {
                "SUPER" => 64,
                "CTRL" => 4,
                "ALT" => 8,
                "SHIFT" => 1,
                "MOD2" => 16,
                "MOD3" => 32,
                "MOD5" => 128,
                _ => 0
            } );

        public void Execute( string source, string keys )
        {
            var parsed = LuaBindingReader.Read( source );
            var definitions = Regex.Matches( source, "hl\\.define_submap\\([\"']([^\"']+)[\"']" ).Select( match =>
                (Start: match.Index, End: source.IndexOf( "\nend)", match.Index, StringComparison.Ordinal ), Name: match.Groups[1].Value) ).ToArray( );
            var events = parsed.Bindings.Select( call => (Offset: call.Offset, Bind: ( LuaBindingCall? ) call, Chord: ( string? ) null, Require: false) )
                .Concat( Regex.Matches( source, "hl\\.unbind\\([\"']([^\"']+)[\"']\\)" ).Select( match => (match.Index, ( LuaBindingCall? ) null,
                     ( string? ) match.Groups[1].Value, false) ) )
                .Concat( parsed.Requires.Where( static require => require.Module == "hypr.commanddeck_keys" ).Select( static require =>
                     (require.Offset, ( LuaBindingCall? ) null, ( string? ) null, true) ) )
                .OrderBy( static entry => entry.Item1 );
            foreach ( var (_, call, chord, require) in events )
            {
                if ( require )
                {
                    Execute( keys, "" );
                    continue;
                }

                if ( chord is not null )
                {
                    _bindings.RemoveAll( bind => bind.Chord == ShortcutLua.CanonicalChord( chord ) );
                    continue;
                }

                var submap = definitions.FirstOrDefault( definition => call!.Offset > definition.Start && call.Offset < definition.End ).Name ?? "";
                _bindings.Add( (ShortcutLua.CanonicalChord( call!.Chord ), submap ==
                     ShortcutLua.RecordingSubmap ? "Record cancel" : call.Description, submap, call.Dispatcher) );
            }
        }

        public void PressEscape( )
        {
            var binding = _bindings.Single( bind => bind.Submap == CurrentSubmap && bind.Chord == "ESCAPE" );
            foreach ( Match action in Regex.Matches( binding.Dispatcher, "hl\\.dsp\\.(submap|global)\\(\"([^\"]+)\"\\)" ) )
            {
                if ( action.Groups[1].Value == "submap" && action.Groups[2].Value == "reset" )
                {
                    CurrentSubmap = "";
                }

                if ( action.Groups[1].Value == "global" )
                {
                    Equal( "", CurrentSubmap );
                    Cancelled = action.Groups[2].Value == "commanddeck:record-cancel";
                }
            }
        }
    }

    private sealed class ShortcutExecutionFixture : IDisposable
    {
        public HotkeyFixture Files { get; } = new( );
        public FakeHyprlandBindings Table { get; } = new( );
        public ShortcutEditorService Service { get; }
        public IReadOnlyList<Keybinding> Inventory => Table.Inventory;
        public Func<ValueTask>? BeforeInventory { get; set; }

        public ShortcutExecutionFixture( string extra )
        {
            PrepareShortcutSources( Files );
            File.WriteAllText( Files.Paths.Bindings, "o.bind('SUPER + Q', 'Menu', 'menu')\no.bind('SUPER + T', 'Terminal', 'terminal')\n" + extra );
            File.WriteAllText( Path.Combine( Files.Root, "omarchy", "default", "hypr", "bindings.lua" ), "" );
            Service = ShortcutService( Files, async ( _, args, _, _ ) =>
            {
                var snapshot = Table.Json;
                if ( args[0] == "-j" && BeforeInventory is not null )
                {
                    await BeforeInventory( );
                }

                if ( args[0] == "reload" )
                {
                    Reload( );
                }

                var output = args[0] switch
                {
                    "-j" => snapshot,
                    "configerrors" => "",
                    _ => "ok"
                };

                return ShortcutProcessResult( output );
            } );
            Reload( );
        }

        public void Reload( )
        {
            Table.Clear( );
            Table.Execute( File.ReadAllText( Files.Paths.Bindings ), "" );
            Table.Execute( File.ReadAllText( Files.Paths.HotkeyFile ), File.Exists( Service.ModuleFile ) ? File.ReadAllText( Service.ModuleFile ) : "" );
        }

        public async Task<Keybinding> RowAsync( string name ) => (await Service.SnapshotAsync( Inventory, default )).Bindings.Single( row =>
             row.Description == name );

        public async Task ChangeAsync( string id, string chord, bool replace = false ) =>
             await Service.ChangeAsync( ProtocolOperations.ShortcutSet, new( ) { Id = id, Chord = chord, Replace = replace }, chord.Length == 0,
             Inventory, default );

        public void Expect( string chord, params string[] names ) => Equal( string.Join( '|', names.Order( StringComparer.Ordinal ) ), Table.Names( chord ) );

        public void Dispose( )
        {
            Service.DisposeAsync( ).AsTask( ).GetAwaiter( ).GetResult( );
            Files.Dispose( );
        }
    }
}

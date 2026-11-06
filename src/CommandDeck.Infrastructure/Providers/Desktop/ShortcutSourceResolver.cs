using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

// Conditional: first loaded through a require that may not run, so the binding
// may not exist, or may load at a later require, in another order.
public sealed record ResolvedShortcut( string Id, string File, LuaBindingCall Call, bool BeforeModule, bool Conditional = false );

// Only reachable literal requires establish load order. Unreachable source files
// still supply a useful path, but never permission to rewrite their bindings, and
// conditional ones never permission to recreate them. The walk follows require
// with a literal name: in place, under a condition or loop, or inside a function.
// A function may run at any time, so a module it requires, and what that module
// requires, may load earlier or later than its place. Other loading, such as
// dofile or pcall(require, ...), is not followed, so the files it loads count as
// unreachable.
public sealed class ShortcutSourceResolver( string hyprDirectory, string omarchyDirectory )
{
    private readonly Dictionary<string, (string Content, LuaSource Source)> _cache = new( StringComparer.Ordinal );

    public IReadOnlyList<ResolvedShortcut> Read( string? mainSource = null )
    {
        List<ResolvedShortcut> result = [];
        Walk walk = new( );
        var before = true;
        Visit( Path.Combine( hyprDirectory, "hyprland.lua" ), result, walk, ref before, reachable: true, conditional: false,
             mainSource is null ? null : LuaBindingReader.Read( mainSource ) );

        // The list grows as modules found inside functions are read.
        for ( var index = 0; index < walk.InFunctions.Count; index++ )
        {
            var file = walk.InFunctions[index];
            if ( !walk.Loads.ContainsKey( file ) )
            {
                var late = false;
                Visit( file, result, walk, ref late, reachable: true, conditional: true );
            }

            Unplace( file, result, walk );
        }

        // Without the module's require, or when it may load out of its place,
        // nothing is known to load before it.
        if ( before || walk.Unordered )
        {
            result = result.Select( static source => source with { BeforeModule = false } ).ToList( );
        }

        foreach ( var root in new[] { hyprDirectory, Path.Combine( omarchyDirectory, "default", "hypr" ) } )
        {
            if ( !Directory.Exists( root ) )
            {
                continue;
            }

            foreach ( var file in Directory.EnumerateFiles( root, "*.lua", SearchOption.AllDirectories ).Order( StringComparer.Ordinal ) )
            {
                var unreachable = false;
                Visit( file, result, walk, ref unreachable, reachable: false, conditional: false );
            }
        }

        return result;
    }

    private void Visit( string file, List<ResolvedShortcut> result, Walk walk, ref bool before, bool reachable, bool conditional,
        LuaSource? sourceOverride = null, bool inLoop = false )
    {
        if ( walk.Loads.ContainsKey( file ) )
        {
            if ( reachable )
            {
                Load( file, result, walk, before );
            }

            return;
        }

        walk.Loads[file] = new( conditional, before && reachable );
        if ( walk.Loads.Count > 512 || !File.Exists( file ) ||
             Path.GetFileName( file ) is "commanddeck.lua" or "commanddeck_keys.lua" || new FileInfo( file ).Length > 1_048_576 )
        {
            return;
        }

        var source = sourceOverride ?? Parse( file );
        walk.Sources[file] = source;
        foreach ( var item in Events( source ) )
        {
            if ( item.Require is { } require )
            {
                Follow( require, result, walk, ref before, reachable, conditional, inLoop );
            }
            else if ( item.Bind is { } bind )
            {
                var id = BindingId( file, bind.Chord, bind.Description );
                result.Add( new( id, file, bind, before && reachable, conditional ) );
            }
        }
    }

    private void Follow( LuaRequire require, List<ResolvedShortcut> result, Walk walk, ref bool before, bool reachable, bool conditional,
        bool inLoop )
    {
        if ( !reachable )
        {
            return;
        }

        if ( require.Module == "hypr.commanddeck" )
        {
            // Only an in-place require marks where the module loads. One inside a
            // function or an expression, or a later pass of a loop, may load it
            // out of its place.
            walk.Unordered |= !require.InPlace || inLoop || require.InLoop;
            if ( require.InPlace )
            {
                before = false;
            }

            return;
        }

        if ( Resolve( require.Module ) is not { } path )
        {
            return;
        }

        if ( require.InFunction )
        {
            if ( !walk.InFunctions.Contains( path ) )
            {
                walk.InFunctions.Add( path );
            }
        }
        else
        {
            Visit( path, result, walk, ref before, reachable, conditional || !require.Ordered, inLoop: inLoop || require.InLoop );
        }
    }

    // A module that a function may load at any time, with every module it
    // requires: their bindings have no known place, and if CommandDeck's module
    // is among them, nothing has.
    private void Unplace( string file, List<ResolvedShortcut> result, Walk walk )
    {
        if ( !walk.Unplaced.Add( file ) )
        {
            return;
        }

        if ( walk.Loads.ContainsKey( file ) )
        {
            walk.Loads[file] = new( Conditional: true, Before: false );
        }

        for ( var index = 0; index < result.Count; index++ )
        {
            if ( result[index].File == file )
            {
                result[index] = result[index] with { BeforeModule = false, Conditional = true };
            }
        }

        // A require inside a function here is listed already; its module is read
        // and unplaced in turn.
        foreach ( var require in walk.Sources.GetValueOrDefault( file )?.Requires.Where( static require => !require.InFunction ) ?? [] )
        {
            if ( require.Module == "hypr.commanddeck" )
            {
                walk.Unordered = true;
            }
            else if ( Resolve( require.Module ) is { } path )
            {
                Unplace( path, result, walk );
            }
        }
    }

    // Lua loads a module once, at the first require that runs. A module first
    // required under a condition may load at this require instead, so it may load
    // as late as this require; it stays conditional, since its order among the
    // other modules depends on the condition. The modules it requires move with it.
    private void Load( string file, List<ResolvedShortcut> result, Walk walk, bool before )
    {
        var current = walk.Loads[file];
        if ( !current.Conditional || !current.Before || before )
        {
            return;
        }

        walk.Loads[file] = current with { Before = false };
        for ( var index = 0; index < result.Count; index++ )
        {
            if ( result[index].File == file )
            {
                result[index] = result[index] with { BeforeModule = false };
            }
        }

        if ( !walk.Sources.TryGetValue( file, out var source ) )
        {
            return;
        }

        foreach ( var require in source.Requires.Where( static require => !require.InFunction ) )
        {
            if ( require.Module == "hypr.commanddeck" )
            {
                walk.Unordered = true;
            }
            else if ( Resolve( require.Module ) is { } path && walk.Loads.ContainsKey( path ) )
            {
                Load( path, result, walk, before: false );
            }
        }
    }

    private static IEnumerable<(int Offset, LuaRequire? Require, LuaBindingCall? Bind)> Events( LuaSource source ) => source.Requires
        .Select( static require => (require.Offset, Require: ( LuaRequire? ) require, Bind: ( LuaBindingCall? ) null) )
        .Concat( source.Bindings.Select( static bind => (bind.Offset, Require: ( LuaRequire? ) null, Bind: ( LuaBindingCall? ) bind) ) )
        .OrderBy( static item => item.Offset );

    // How a module may load: Conditional when its first require may not run,
    // Before when it loads before CommandDeck's module if it loads.
    private readonly record struct ModuleLoad( bool Conditional, bool Before );

    // What one Read has seen: how each module it visited may load, the sources it
    // read, the modules required inside functions and those already unplaced, and
    // whether CommandDeck's module may load out of its place.
    private sealed class Walk
    {
        public Dictionary<string, ModuleLoad> Loads { get; } = new( StringComparer.Ordinal );
        public Dictionary<string, LuaSource> Sources { get; } = new( StringComparer.Ordinal );
        public List<string> InFunctions { get; } = [];
        public HashSet<string> Unplaced { get; } = new( StringComparer.Ordinal );
        public bool Unordered { get; set; }
    }

    public static string BindingId( string file, string chord, string description, string submap = "" )
    {
        var identity = file + "\n" + ShortcutLua.CanonicalChord( chord ) + "\n" + description + (submap.Length == 0 ? "" : "\n" + submap);

        return Convert.ToHexString( SHA256.HashData( Encoding.UTF8.GetBytes( identity ) ) );
    }

    private LuaSource Parse( string path )
    {
        var content = File.ReadAllText( path );
        if ( _cache.TryGetValue( path, out var cached ) && cached.Content == content )
        {
            return cached.Source;
        }

        var source = LuaBindingReader.Read( content );
        _cache[path] = (content, source);

        return source;
    }

    private string? Resolve( string module )
    {
        // Lua module names may contain hyphens, as Omarchy's default.hypr.bindings.input-method does.
        if ( module.Split( '.' ).Any( static part => part.Length == 0 || !part.All( static character => char.IsAsciiLetterOrDigit( character ) ||
             character is '_' or '-' ) ) )
        {
            return null;
        }

        var relative = module.Replace( '.', Path.DirectorySeparatorChar ) + ".lua";

        return module.StartsWith( "hypr.", StringComparison.Ordinal ) ? Path.Combine( hyprDirectory,
             relative[5..] ) : Path.Combine( omarchyDirectory, relative );
    }
}

using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

public sealed record ResolvedShortcut( string Id, string File, LuaBindingCall Call, bool BeforeModule );

// Only reachable literal requires establish load order. Unreachable source files
// still supply a useful path, but never permission to rewrite their bindings.
public sealed class ShortcutSourceResolver( string hyprDirectory, string omarchyDirectory )
{
    private readonly Dictionary<string, (string Content, LuaSource Source)> _cache = new( StringComparer.Ordinal );

    public IReadOnlyList<ResolvedShortcut> Read( string? mainSource = null )
    {
        List<ResolvedShortcut> result = [];
        HashSet<string> visited = new( StringComparer.Ordinal );
        var before = true;
        Visit( Path.Combine( hyprDirectory, "hyprland.lua" ), result, visited, ref before, reachable: true,
             mainSource is null ? null : LuaBindingReader.Read( mainSource ) );
        if ( before )
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
                Visit( file, result, visited, ref unreachable, reachable: false );
            }
        }

        return result;
    }

    private void Visit( string file, List<ResolvedShortcut> result, HashSet<string> visited, ref bool before, bool reachable, LuaSource? sourceOverride = null )
    {
        if ( visited.Count >= 512 || !visited.Add( file ) || !File.Exists( file ) ||
             Path.GetFileName( file ) is "commanddeck.lua" or "commanddeck_keys.lua" || new FileInfo( file ).Length > 1_048_576 )
        {
            return;
        }

        var source = sourceOverride ?? Parse( file );
        var events = source.Requires.Where( static require => require.Ordered ).Select( static require => (require.Offset,
             Require: ( LuaRequire? ) require, Bind: ( LuaBindingCall? ) null) )
            .Concat( source.Bindings.Select( static bind => (bind.Offset, Require: ( LuaRequire? ) null, Bind: ( LuaBindingCall? ) bind) ) )
            .OrderBy( static item => item.Offset );
        foreach ( var item in events )
        {
            if ( item.Require is { } require )
            {
                if ( require.Module == "hypr.commanddeck" )
                {
                    before = false;
                    continue;
                }

                if ( reachable && Resolve( require.Module ) is { } path )
                {
                    Visit( path, result, visited, ref before, reachable );
                }
            }
            else if ( item.Bind is { } bind )
            {
                var id = BindingId( file, bind.Chord, bind.Description );
                result.Add( new( id, file, bind, before && reachable ) );
            }
        }
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
        if ( module.Split( '.' ).Any( static part => part.Length == 0 || !part.All( static character => char.IsAsciiLetterOrDigit( character ) ||
             character == '_' ) ) )
        {
            return null;
        }

        var relative = module.Replace( '.', Path.DirectorySeparatorChar ) + ".lua";

        return module.StartsWith( "hypr.", StringComparison.Ordinal ) ? Path.Combine( hyprDirectory,
             relative[5..] ) : Path.Combine( omarchyDirectory, relative );
    }
}

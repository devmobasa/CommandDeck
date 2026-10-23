using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

internal sealed partial class InotifyTreeWatcher
{
    // A watched directory. Only its name is stored; full paths are rebuilt
    // from the root, which keeps a large tree of watches compact.
    private sealed class WatchNode( int watch, WatchNode? parent, string name, string? canonicalOverride )
    {
        private Dictionary<string, WatchNode>? _children;

        public int Watch { get; } = watch;
        public WatchNode? Parent { get; } = parent;
        public string Name { get; } = name;
        public string? CanonicalOverride { get; } = canonicalOverride;

        public IEnumerable<WatchNode> Children => _children?.Values ?? Enumerable.Empty<WatchNode>( );

        public WatchNode? GetChild( string name ) =>
            _children is not null && _children.TryGetValue( name, out var child ) ? child : null;

        public void SetChild( WatchNode child ) =>
            (_children ??= new( StringComparer.Ordinal ))[child.Name] = child;

        public void RemoveChild( WatchNode child )
        {
            if ( IsRegisteredChild( child ) )
            {
                _children.Remove( child.Name );
            }
        }

        [MemberNotNullWhen( true, nameof( _children ) )]
        private bool IsRegisteredChild( WatchNode child )
        {
            return _children is not null && _children.TryGetValue( child.Name, out var existing ) && ReferenceEquals( existing, child );
        }
    }
}

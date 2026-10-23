namespace CommandDeck;

public sealed partial class CommandDeckRuntime
{
    private IQuicklinkStore Quicklinks
    {
        get
        {
            var store = _catalog as IQuicklinkStore;
            if ( store is null )
            {
                throw new InvalidOperationException( "Quicklink management is unavailable." );
            }

            return store;
        }
    }

    public async ValueTask<QuicklinksResult> GetQuicklinksAsync( CancellationToken cancellationToken )
    {
        if ( _catalog is IQuicklinkStore store )
        {
            return await store
                .GetQuicklinksAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        var preferences = await _preferences
            .GetPreferencesAsync( cancellationToken )
            .ConfigureAwait( false );

        return new QuicklinksResult { Quicklinks = GetConfiguredQuicklinks( ), WebFallbackQuicklinkId = preferences.WebFallbackQuicklinkId };
    }

    public ValueTask<QuicklinksResult> RestoreQuicklinksAsync( CustomizationExport backup, CancellationToken cancellationToken ) =>
        Quicklinks.RestoreQuicklinksAsync( backup.Quicklinks, backup.Preferences.WebFallbackQuicklinkId, cancellationToken );

    public ValueTask<QuicklinksResult> ChangeQuicklinkAsync( string action, QuicklinkOptions? quicklink, string? id, CancellationToken cancellationToken ) =>
        Quicklinks.ChangeQuicklinkAsync( action, quicklink, id, cancellationToken );

    private List<QuicklinkEntry> GetConfiguredQuicklinks( )
    {
        return _options
            .Quicklinks.Select( link => new QuicklinkEntry { Quicklink = link, Source = QuicklinkSources.Configured } )
            .ToList( );
    }
}

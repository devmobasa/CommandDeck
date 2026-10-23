namespace CommandDeck;

public sealed record ClipboardPolicyChange(
    ClipboardHistoryPolicy Before,
    ClipboardHistoryPolicy Proposed,
    long StoreRevision,
    IReadOnlyList<string> DeleteIds,
    int PinnedCount,
    long RetainedBytes,
    bool RequiresConfirmation );

public sealed partial class ClipboardHistoryStore
{
    private bool _startupClearCompleted;

    public ClipboardPolicyChange PreparePolicyChange( long expectedRevision, ClipboardPolicyPatch patch )
    {
        lock ( _gate )
        {
            var current = ReadPolicy( );
            if ( current.Revision != expectedRevision )
            {
                throw new InvalidOperationException( "Clipboard settings changed; refresh them before saving." );
            }

            var candidate = ApplyPolicyPatch( current, patch );
            ValidatePolicyCandidate( candidate );
            var rows = PolicyRows( );

            var pins = rows
                .Where( static row => row.Pinned )
                .ToArray( );

            if ( ExceedsPinPolicyLimits( pins, candidate ) )
            {
                throw new InvalidOperationException( "Clipboard limits cannot be lower than the pinned allocation; unpin entries first." );
            }

            var cutoff = _time
                .GetUtcNow( )
                .AddHours( -candidate.RetentionHours )
                .ToUnixTimeSeconds( );

            var kept = RetainedPolicyEntryIds( rows, cutoff, candidate, pins );

            var deleted = rows
                .Where( row => !row.Pinned && !kept.Contains( row.Id ) )
                .Select( static row => row.Id )
                .ToArray( );

            var retainedBytes = rows
                .Where( row => row.Pinned || kept.Contains( row.Id ) )
                .Sum( static row => row.Bytes );

            ValidateQuotaChange( current, candidate, retainedBytes );
            var confirmation = ReducesPolicyLimits( candidate, current, deleted );

            return new( current, candidate, StoreRevision, deleted, pins.Length, retainedBytes,
                confirmation );
        }
    }

    private static ClipboardHistoryPolicy ApplyPolicyPatch( ClipboardHistoryPolicy current, ClipboardPolicyPatch patch )
    {
        return current with
        {
            Revision = checked(current.Revision + 1),
            Consent = patch.Consent ?? current.Consent,
            Paused = patch.Paused ?? current.Paused,
            MaximumEntries = patch.MaximumEntries ?? current.MaximumEntries,
            RetentionHours = patch.RetentionHours ?? current.RetentionHours,
            QuotaMiB = patch.QuotaMiB ?? current.QuotaMiB,
            ClearOnStartup = patch.ClearOnStartup ?? current.ClearOnStartup
        };
    }

    private static bool ExceedsPinPolicyLimits( PolicyRow[] pins, ClipboardHistoryPolicy candidate )
    {
        return pins.Length > candidate.MaximumEntries / 2 || pins.Sum( static row => row.Bytes ) > ( long ) candidate.QuotaMiB * ByteUnits.BytesPerMebibyte / 2;
    }

    private static HashSet<string> RetainedPolicyEntryIds( List<PolicyRow> rows, long cutoff, ClipboardHistoryPolicy candidate, PolicyRow[] pins )
    {
        return rows
            .Where( row => !row.Pinned && row.Copied >= cutoff )
            .Take( candidate.MaximumEntries - pins.Length )
            .Select( static row => row.Id )
            .ToHashSet( StringComparer.Ordinal );
    }

    private static bool ReducesPolicyLimits( ClipboardHistoryPolicy candidate, ClipboardHistoryPolicy current, string[] deleted )
    {
        return candidate.MaximumEntries < current.MaximumEntries ||
               candidate.RetentionHours < current.RetentionHours ||
               candidate.QuotaMiB < current.QuotaMiB ||
               candidate.ClearOnStartup && !current.ClearOnStartup ||
               candidate.Consent > current.Consent ||
               deleted.Length > 0;
    }

    public ClipboardHistoryPolicy ApplyPolicyChange( ClipboardPolicyChange change, bool confirmed )
    {
        lock ( _gate )
        {
            if ( NeedsPolicyConfirmation( change, confirmed ) )
            {
                throw new InvalidOperationException( "Confirm the clipboard settings preview before saving." );
            }

            var policy = Transaction( ( ) =>
            {
                if ( HasPolicyChangeConflict( change ) )
                {
                    throw new InvalidOperationException( "Clipboard settings preview changed; request a new preview." );
                }

                var proposed = change.Proposed;
                ValidatePolicyCandidate( proposed );

                Execute( """
                         UPDATE clipboard_policy SET revision=$revision,consent=$consent,paused=$paused,
                             maximum_entries=$maximum,retention_hours=$age,quota_mib=$quota,clear_on_startup=$clear
                         """, ("$revision", proposed.Revision), ("$consent", ( int ) proposed.Consent), ("$paused", proposed.Paused ? 1 : 0),
                    ("$maximum", proposed.MaximumEntries), ("$age", proposed.RetentionHours), ("$quota", proposed.QuotaMiB),
                    ("$clear", proposed.ClearOnStartup ? 1 : 0) );

                foreach ( var id in change.DeleteIds )
                {
                    Execute( "DELETE FROM entries WHERE id=$id AND pinned=0", ("$id", id) );
                }

                if ( change.DeleteIds.Count > 0 )
                {
                    AdvanceRevision( );
                }

                return ReadPolicy( );
            } );

            AdvanceCaptureEpoch( );
            _payloadLedger!.Cleanup( );

            return policy;
        }
    }

    private static bool NeedsPolicyConfirmation( ClipboardPolicyChange change, bool confirmed )
    {
        return change.RequiresConfirmation && !confirmed;
    }

    private bool HasPolicyChangeConflict( ClipboardPolicyChange change )
    {
        return ReadPolicy( ) != change.Before || StoreRevision != change.StoreRevision;
    }

    private static void ValidatePolicyCandidate( ClipboardHistoryPolicy policy )
    {
        if ( !Enum.IsDefined( policy.Consent ) ||
             policy is { Consent: ClipboardConsent.None, Paused: true } ||
             policy.MaximumEntries is < OptionLimits.ClipboardHistory.MaximumEntries.Minimum or > OptionLimits.ClipboardHistory.MaximumEntries.Maximum ||
             policy.RetentionHours is < OptionLimits.ClipboardHistory.RetentionHours.Minimum or > OptionLimits.ClipboardHistory.RetentionHours.Maximum ||
             policy.QuotaMiB is < OptionLimits.ClipboardHistory.QuotaMiB.Minimum or > OptionLimits.ClipboardHistory.QuotaMiB.Maximum )
        {
            throw new InvalidOperationException( "Clipboard settings are outside supported limits." );
        }
    }

    private void ValidateQuotaChange( ClipboardHistoryPolicy current, ClipboardHistoryPolicy candidate, long retainedBytes )
    {
        var quota = ( long ) candidate.QuotaMiB * ByteUnits.BytesPerMebibyte;
        if ( ExceedsChangedQuota( retainedBytes, quota, candidate, current ) )
        {
            throw new InvalidOperationException( "Clipboard payload quota is below the retained or pending cleanup allocation." );
        }
    }

    private bool ExceedsChangedQuota( long retainedBytes, long quota, ClipboardHistoryPolicy candidate, ClipboardHistoryPolicy current )
    {
        return retainedBytes > quota || candidate.QuotaMiB < current.QuotaMiB && _payloadLedger!.ChargedBytes > quota;
    }

    private List<PolicyRow> PolicyRows( )
    {
        using var command = _connection.CreateCommand( );

        command.CommandText =
            "SELECT id,(SELECT coalesce(sum(p.actual),0) FROM clipboard_offers o JOIN clipboard_payloads p ON p.id=o.payload_id WHERE o.entry_id=e.id),copied,pinned FROM entries e ORDER BY copied DESC,rowid DESC";

        using var reader = command.ExecuteReader( );
        List<PolicyRow> rows = [];

        while ( reader.Read( ) )
        {
            rows.Add( new( reader.GetString( 0 ), reader.GetInt64( 2 ), reader.GetBoolean( 3 ), reader.GetInt64( 1 ) ) );
        }

        return rows;
    }

    private void CompleteStartupClear( )
    {
        if ( ShouldSkipStartupClear( ) )
        {
            return;
        }

        if ( ReadPolicy( ).ClearOnStartup )
        {
            Clear( );
        }

        _startupClearCompleted = true;
    }

    private bool ShouldSkipStartupClear( )
    {
        return _startupClearCompleted || MigrationRequired( );
    }

    private sealed record PolicyRow( string Id, long Copied, bool Pinned, long Bytes );
}

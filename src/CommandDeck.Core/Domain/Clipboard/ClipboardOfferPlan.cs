namespace CommandDeck;

public enum ClipboardOfferKind
{
    GnomeCopiedFiles, PlainText, UriList, Html, Png, ChromiumSourceUrl
}

public enum ClipboardInventoryRejection
{
    None, CaptureDisabled, InvalidInventory, SensitiveHint, SelfWrite, AliasLimit, Unsupported, ByteLimit
}

public sealed record ClipboardPlannedOffer( int InventoryIndex, string RawName, ClipboardOfferKind Kind, int MaximumBytes, bool HeaderOnly );

/// <summary>Immutable inventory admission; constructing a plan never reads offer content.</summary>
public sealed class ClipboardOfferPlan
{
    public const string OwnerMime = "application/x-commanddeck-owner-v1";
    public const int MaximumSelectionBytes = 24 * ByteUnits.BytesPerMebibyte;
    public IReadOnlyList<ClipboardPlannedOffer> Offers { get; }
    public int ReservedBytes { get; }
    public ClipboardInventoryRejection Rejection { get; }
    public bool Accepted => Rejection == ClipboardInventoryRejection.None;

    private ClipboardOfferPlan( ClipboardInventoryRejection rejection, ClipboardPlannedOffer[] offers )
    {
        Rejection = rejection;
        Offers = Array.AsReadOnly( offers );
        ReservedBytes = offers.Sum( static offer => offer.MaximumBytes );
    }

    public static ClipboardOfferPlan Create( IReadOnlyList<string> inventory, ClipboardConsent consent, bool chromiumSourceFixturesVerified = false )
    {
        if ( IsInvalidInventory( inventory, consent ) )
        {
            return Reject( ClipboardInventoryRejection.InvalidInventory );
        }

        HashSet<string> rawNames = new( StringComparer.Ordinal );
        Dictionary<ClipboardOfferKind, int> counts = [];
        List<ClipboardPlannedOffer> supported = [];
        for ( var index = 0; index < inventory.Count; index++ )
        {
            var raw = inventory[index];
            if ( IsInvalidMimeName( raw ) )
            {
                return Reject( ClipboardInventoryRejection.InvalidInventory );
            }

            // Chromium-based browsers advertise the same name twice; the first occurrence is the one requested.
            if ( !rawNames.Add( raw ) )
            {
                continue;
            }

            var hint = CheckHint( raw );
            if ( hint != ClipboardInventoryRejection.None )
            {
                return Reject( hint );
            }

            if ( !TryClassify( raw, out var kind ) )
            {
                continue;
            }

            var count = counts.GetValueOrDefault( kind ) + 1;
            counts[kind] = count;

            if ( count > AliasLimit( kind ) )
            {
                return Reject( ClipboardInventoryRejection.AliasLimit );
            }

            supported.Add( new( index, raw, kind, MaximumBytes( kind ), false ) );
        }

        if ( consent == ClipboardConsent.None )
        {
            return Reject( ClipboardInventoryRejection.CaptureDisabled );
        }

        var offers = SelectOffers( consent, chromiumSourceFixturesVerified, supported );
        if ( HasNoContentOffer( offers ) )
        {
            return Reject( ClipboardInventoryRejection.Unsupported );
        }

        if ( ExceedsSelectionLimits( offers ) )
        {
            return Reject( ClipboardInventoryRejection.ByteLimit );
        }

        return new( ClipboardInventoryRejection.None, offers );
    }

    private static ClipboardOfferPlan Reject( ClipboardInventoryRejection reason ) => new( reason, [] );

    private static ClipboardInventoryRejection CheckHint( string raw )
    {
        var name = NormalizeMimeName( raw );

        return name switch
        {
            "x-kde-passwordmanagerhint" or "application/x-nspasteboard-concealed-type" or "excludeclipboardcontentfrommonitorprocessing" =>
                ClipboardInventoryRejection.SensitiveHint,
            OwnerMime => ClipboardInventoryRejection.SelfWrite,
            _ => ClipboardInventoryRejection.None
        };
    }

    private static bool TryClassify( string raw, out ClipboardOfferKind kind )
    {
        kind = default;
        var parts = raw.Split( ';' );

        var name = parts[0]
            .Trim( );

        if ( HasInvalidMimeParameters( parts, name ) )
        {
            return false;
        }

        ClipboardOfferKind? match = GetOfferKind( name );
        if ( match is null )
        {
            return false;
        }

        kind = match.Value;

        return true;
    }

    private static bool ValidCharset( string name, string parameter )
    {
        if ( IsNonTextMime( name ) )
        {
            return false;
        }

        var pair = parameter.Split( '=', 2 );

        return pair.Length == 2 &&
               pair[0]
                   .Trim( )
                   .Equals( "charset", StringComparison.OrdinalIgnoreCase ) &&
               pair[1]
                   .Trim( )
                   .Equals( "utf-8", StringComparison.OrdinalIgnoreCase );
    }

    private static int AliasLimit( ClipboardOfferKind kind ) => kind switch
    {
        ClipboardOfferKind.PlainText or ClipboardOfferKind.Html or ClipboardOfferKind.UriList => 2,
        _ => 1
    };

    private static int MaximumBytes( ClipboardOfferKind kind ) => kind switch
    {
        ClipboardOfferKind.PlainText => 32 * ByteUnits.BytesPerKibibyte,
        ClipboardOfferKind.Html => ByteUnits.BytesPerMebibyte,
        ClipboardOfferKind.Png => ClipboardOfferLimits.MaximumPngBytes,
        ClipboardOfferKind.ChromiumSourceUrl => 8192,
        _ => 64 * ByteUnits.BytesPerKibibyte
    };

    private static bool IsInvalidInventory( IReadOnlyList<string> inventory, ClipboardConsent consent )
    {
        return inventory.Count > ClipboardOfferLimits.MaximumInventoryNames || !Enum.IsDefined( consent );
    }

    private static bool IsInvalidMimeName( string raw )
    {
        return string.IsNullOrEmpty( raw ) ||
               raw.Length > ClipboardOfferLimits.MaximumMimeNameCharacters ||
               raw.Any( static character => character is < ' ' or > '~' );
    }

    private static ClipboardPlannedOffer[] SelectOffers( ClipboardConsent consent, bool chromiumSourceFixturesVerified, List<ClipboardPlannedOffer> supported )
    {
        return supported
            .Where( offer => consent == ClipboardConsent.Rich || offer.Kind is ClipboardOfferKind.PlainText or ClipboardOfferKind.GnomeCopiedFiles )
            .Where( offer => chromiumSourceFixturesVerified || offer.Kind != ClipboardOfferKind.ChromiumSourceUrl )
            .Select( offer => LimitOfferForConsent( consent, offer ) )
            .OrderBy( static offer => offer.Kind )
            .ThenBy( static offer => offer.RawName, StringComparer.Ordinal )
            .ToArray( );
    }

    private static bool HasNoContentOffer( ClipboardPlannedOffer[] offers )
    {
        return !offers.Any( static offer => !offer.HeaderOnly && offer.Kind != ClipboardOfferKind.ChromiumSourceUrl );
    }

    private static bool ExceedsSelectionLimits( ClipboardPlannedOffer[] offers )
    {
        return offers.Length > ClipboardOfferLimits.MaximumOffers || offers.Sum( static offer => ( long ) offer.MaximumBytes ) > MaximumSelectionBytes;
    }

    private static string NormalizeMimeName( string raw )
    {
        return raw
            .Split( ';', 2 )[0]
            .Trim( )
            .ToLowerInvariant( );
    }

    private static bool HasInvalidMimeParameters( string[] parts, string name )
    {
        return parts.Length > 2 || parts.Length == 2 && !ValidCharset( name, parts[1] );
    }

    private static ClipboardOfferKind? GetOfferKind( string name )
    {
        return name.ToLowerInvariant( ) switch
        {
            "text/plain" => ClipboardOfferKind.PlainText,
            "text/html" => ClipboardOfferKind.Html,
            "image/png" => ClipboardOfferKind.Png,
            "text/uri-list" => ClipboardOfferKind.UriList,
            "x-special/gnome-copied-files" => ClipboardOfferKind.GnomeCopiedFiles,
            "chromium/x-source-url" => ClipboardOfferKind.ChromiumSourceUrl,
            _ => null
        };
    }

    private static bool IsNonTextMime( string name )
    {
        return !name.Equals( "text/plain", StringComparison.OrdinalIgnoreCase ) && !name.Equals( "text/html", StringComparison.OrdinalIgnoreCase );
    }

    private static ClipboardPlannedOffer LimitOfferForConsent( ClipboardConsent consent, ClipboardPlannedOffer offer )
    {
        if ( consent == ClipboardConsent.Text && offer.Kind == ClipboardOfferKind.GnomeCopiedFiles )
        {
            return offer with { MaximumBytes = ClipboardGnomeHeader.MaximumBytes, HeaderOnly = true };
        }

        return offer;
    }
}

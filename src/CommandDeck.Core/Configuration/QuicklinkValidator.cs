namespace CommandDeck;

public static class QuicklinkValidator
{
    internal const int MaximumIdCharacters = 64;
    public const int MaximumTitleCharacters = 64;
    public const int MaximumIconCharacters = 256;
    public const int MaximumTemplateCharacters = 2048;

    public static readonly string TitleMessage = $"Quicklink title must be 1-{MaximumTitleCharacters} characters without controls.";
    public static readonly string UrlLengthMessage = $"Quicklink URL must be 1-{MaximumTemplateCharacters} characters without controls.";
    public const string UrlMessage = "Quicklink URL must be an absolute HTTP or HTTPS URL without credentials; only {query} placeholders are supported.";
    public static readonly string IconMessage = $"Quicklink icon must be 1-{MaximumIconCharacters} characters without controls.";

    public static void Validate( QuicklinkOptions link )
    {
        if ( link is null )
        {
            throw new InvalidOperationException( "quicklinks must contain valid objects." );
        }

        if ( HasInvalidQuicklinkId( link ) )
        {
            throw new InvalidOperationException(
                $"Quicklink ID must use 1-{MaximumIdCharacters} ASCII letters, digits, dots, underscores, or hyphens, and must start with a letter or digit." );
        }

        if ( HasInvalidQuicklinkTitle( link ) )
        {
            throw new InvalidOperationException( TitleMessage );
        }

        ValidateUrl( link.UrlTemplate );

        if ( link.Alias is not null )
        {
            var alias = AliasValidator.NormalizeAndValidate( link.Alias );
            if ( alias != link.Alias )
            {
                throw new InvalidOperationException( AliasValidator.InvalidMessage );
            }
        }

        if ( HasInvalidQuicklinkIcon( link ) )
        {
            throw new InvalidOperationException( IconMessage );
        }
    }

    private static void ValidateUrl( string template )
    {
        if ( IsInvalidUrlTemplate( template ) )
        {
            throw new InvalidOperationException( UrlLengthMessage );
        }

        var expanded = UrlTemplateTokens.Expand( template, "sample" );
        if ( IsInvalidExpandedUrl( expanded ) )
        {
            throw new InvalidOperationException( UrlMessage );
        }
    }

    public static void ValidateUnique( IEnumerable<QuicklinkOptions> links )
    {
        HashSet<string> ids = new( StringComparer.OrdinalIgnoreCase );
        HashSet<string> aliases = new( StringComparer.OrdinalIgnoreCase );
        foreach ( var link in links )
        {
            Validate( link );

            if ( !ids.Add( link.Id ) )
            {
                throw new InvalidOperationException( $"Quicklink ID '{link.Id}' is configured more than once." );
            }

            if ( link.Alias is not null && !aliases.Add( link.Alias ) )
            {
                throw new InvalidOperationException( $"Quicklink alias '{link.Alias}' is configured more than once." );
            }
        }
    }

    private static bool HasInvalidQuicklinkId( QuicklinkOptions link )
    {
        return string.IsNullOrEmpty( link.Id ) ||
               link.Id.Length > MaximumIdCharacters ||
               !char.IsAsciiLetterOrDigit( link.Id[0] ) ||
               link.Id.Any( character => !char.IsAsciiLetterOrDigit( character ) && character is not '.' and not '_' and not '-' );
    }

    private static bool HasInvalidQuicklinkTitle( QuicklinkOptions link )
    {
        return string.IsNullOrWhiteSpace( link.Title ) || link.Title.Length > MaximumTitleCharacters || link.Title.Any( char.IsControl );
    }

    private static bool HasInvalidQuicklinkIcon( QuicklinkOptions link )
    {
        return link.Icon is not null && (link.Icon.Length is < 1 or > MaximumIconCharacters || link.Icon.Any( char.IsControl ));
    }

    private static bool IsInvalidUrlTemplate( string template )
    {
        return string.IsNullOrWhiteSpace( template ) || template.Length > MaximumTemplateCharacters || template.Any( char.IsControl );
    }

    private static bool IsInvalidExpandedUrl( string expanded )
    {
        return expanded.Contains( '{' ) ||
               expanded.Contains( '}' ) ||
               !Uri.TryCreate( expanded, UriKind.Absolute, out var uri ) ||
               uri.Scheme is not (UrlSchemes.Http or UrlSchemes.Https) ||
               string.IsNullOrEmpty( uri.Host ) ||
               !string.IsNullOrEmpty( uri.UserInfo );
    }
}

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommandDeck.Extensions.Views;

namespace CommandDeck.ExtensionProtocol;

public static partial class ManifestValidation
{
    private const int MaximumCapabilities = 16;
    private const int MaximumPreferences = 16;
    private const int MaximumRequiredFeatures = 16;
    private const int MaximumJsonDepth = 16;
    private const int MaximumPackageIdCharacters = 128;
    private const int MaximumPackagePathCharacters = 512;
    private const int MaximumKeywords = 32;
    private const int MaximumLabelBytes = 512;
    private const int MaximumDescriptionBytes = 2048;
    private const int MaximumLicenseBytes = 128;
    private const int MaximumProviderTitleBytes = 128;
    private const int MaximumKeywordBytes = 128;
    private const int MaximumCapabilityReasonBytes = 2048;
    private const int MaximumChoiceValueBytes = 256;
    private const int MaximumVersionCharacters = 64;
    private const string RestrictedExecutionProfile = "restricted";
    private const int MaximumSearchProviders = 4;
    private const int PreReleaseCaptureGroupIndex = 4;
    private const int MaximumSuggestedPrefixCharacters = 20;
    private const int MaximumPreferenceBytes = 8192;
    private const int MaximumPreferenceChoices = 100;
    private const int MaximumPathSegments = 12;
    public const int MaximumManifestBytes = 65536;
    public const int MaximumPackages = 128;
    public const string RuntimeProfile = ExtensionTargets.LinuxGlibcInvariantV1;
    public static readonly IReadOnlySet<string> KnownFeatures = ExtensionFeatures.Interactive.ToFrozenSet( StringComparer.Ordinal );

    public static readonly IReadOnlySet<string> KnownCapabilities = new HashSet<string>( StringComparer.Ordinal )
    {
        ExtensionPermissionIds.ClipboardWrite,
        ExtensionPermissionIds.DesktopOpenUrl,
        ExtensionPermissionIds.StorageRead,
        ExtensionPermissionIds.StorageWrite,
        ExtensionPermissionIds.FilesRead,
        ExtensionPermissionIds.HttpRequest,
        ExtensionPermissionIds.SecretsUse
    };

    [GeneratedRegex( "^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*\\.[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant )]
    private static partial Regex PackageIdPattern( );

    [GeneratedRegex(
        "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant )]
    private static partial Regex VersionPattern( );

    public static ExtensionManifest Parse( ReadOnlyMemory<byte> bytes )
    {
        if ( bytes.Length > MaximumManifestBytes )
        {
            Invalid( "The manifest exceeds 64 KiB." );
        }

        using var document = StrictJson.Parse( bytes, MaximumJsonDepth );
        var manifest = document.RootElement.Deserialize( ExtensionManifestJsonContext.Default.ExtensionManifest );
        if ( manifest is null )
        {
            throw new InvalidDataException( "The extension manifest is missing." );
        }

        Validate( manifest );

        return manifest;
    }

    public static void Validate( ExtensionManifest manifest )
    {
        ArgumentNullException.ThrowIfNull( manifest );

        if ( manifest.ManifestVersion != 1 )
        {
            Invalid( "The manifest version is unsupported." );
        }

        if ( HasInvalidPackageId( manifest ) )
        {
            Invalid( "The package ID is invalid." );
        }

        ValidateVersion( manifest.Version );
        Label( manifest.Name, MaximumLabelBytes );
        Label( manifest.Description, MaximumDescriptionBytes );
        Label( manifest.License, MaximumLicenseBytes );

        if ( HasUnsupportedProtocol( manifest ) )
        {
            Invalid( "The protocol or execution profile is unsupported." );
        }

        Features( manifest.RequiredFeatures );

        if ( ExceedsContributionLimits( manifest ) )
        {
            Invalid( "The manifest contributions exceed their limits." );
        }

        ValidateTargets( manifest.Targets! );
        ValidateCapabilities( manifest.Capabilities! );
        ValidateCommands( manifest.Commands!, manifest.Capabilities! );
        ValidatePreferences( manifest.Preferences! );
        ValidateSearchProviders( manifest );
    }

    private static void ValidateSearchProviders( ExtensionManifest manifest )
    {
        if ( ExceedsSearchProviderLimit( manifest ) )
        {
            Invalid( "At most four search providers are allowed." );
        }

        HashSet<string> ids = new( StringComparer.Ordinal );

        foreach ( var provider in manifest.SearchProviders! )
        {
            if ( provider is null )
            {
                Invalid( "A search provider is missing." );
            }

            ViewValidation.ValidateId( provider!.Id );
            ViewValidation.ValidateId( provider.CacheKey );
            Label( provider.Title, MaximumProviderTitleBytes );

            if ( !ids.Add( provider.Id ) || HasInvalidSearchProvider( manifest, provider ) )
            {
                Invalid( "Invalid search provider command or suggested prefix." );
            }
        }
    }

    private static void ValidateTargets( Dictionary<string, ExtensionTarget> targets )
    {
        foreach ( var (rid, target) in targets )
        {
            if ( HasUnsupportedTarget( rid, target ) )
            {
                Invalid( "The extension deployment target is unsupported." );
            }

            ValidatePackagePath( target!.Entrypoint );

            if ( !target.Entrypoint.StartsWith( "bin/" + rid + "/", StringComparison.Ordinal ) )
            {
                Invalid( "Entrypoints must be under their target's bin directory." );
            }
        }
    }

    private static void ValidateCapabilities( Dictionary<string, CapabilityRequest> capabilities )
    {
        foreach ( var (capability, request) in capabilities )
        {
            if ( HasUnsupportedCapability( capability, request ) )
            {
                Invalid( "The capability is unsupported." );
            }

            Label( request!.Reason, MaximumCapabilityReasonBytes );
        }
    }

    private static void ValidateCommands( ImmutableArray<ExtensionCommand> commands, Dictionary<string, CapabilityRequest> capabilities )
    {
        HashSet<string> ids = new( StringComparer.Ordinal );

        foreach ( var command in commands )
        {
            if ( command is null )
            {
                Invalid( "A command is missing." );
            }

            ViewValidation.ValidateId( command!.Id );

            if ( !ids.Add( command.Id ) || command.Mode is not (ExtensionCommandModes.View or ExtensionCommandModes.NoView) )
            {
                Invalid( "A command ID or mode is invalid." );
            }

            Label( command.Title, MaximumLabelBytes );
            Label( command.Description, MaximumDescriptionBytes, allowEmpty: true );
            Features( command.RequiredFeatures );

            if ( command.Icon is not null )
            {
                ValidatePackagePath( command.Icon );
            }

            if ( ExceedsCommandLimits( command ) )
            {
                Invalid( "A command exceeds its limits." );
            }

            foreach ( var keyword in command.Keywords! )
            {
                Label( keyword, MaximumKeywordBytes );
            }

            if ( HasInvalidCommandCapabilities( capabilities, command ) )
            {
                Invalid( "A command requests an undeclared or duplicate capability." );
            }
        }
    }

    private static void ValidatePreferences( ImmutableArray<ExtensionPreference> preferences )
    {
        HashSet<string> ids = new( StringComparer.Ordinal );

        foreach ( var preference in preferences )
        {
            if ( preference is null )
            {
                Invalid( "A preference is missing." );
            }

            ViewValidation.ValidateId( preference!.Id );

            if ( !ids.Add( preference.Id ) || HasInvalidPreference( preference ) )
            {
                Invalid( "A preference is invalid." );
            }

            Label( preference.Title, MaximumLabelBytes );
            Label( preference.Description, MaximumDescriptionBytes, allowEmpty: true );
            HashSet<string> choices = new( StringComparer.Ordinal );

            foreach ( var choice in preference.Choices! )
            {
                if ( choice is null || !choices.Add( choice.Value ) )
                {
                    Invalid( "A preference choice is invalid." );
                }

                Label( choice!.Value, MaximumChoiceValueBytes );
                Label( choice.Label, MaximumLabelBytes );
            }

            if ( HasSecretDefault( preference ) )
            {
                Invalid( "Secret preferences cannot contain defaults." );
            }

            if ( preference.Default is { } value )
            {
                ValidatePreferenceValue( preference, value );
            }
        }
    }

    public static void ValidatePreferenceValue( ExtensionPreference preference, JsonElement value )
    {
        switch ( preference.Type )
        {
            case CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return;
            case CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Number
                when value.ValueKind == JsonValueKind.Number && value.TryGetDouble( out var number ) && double.IsFinite( number ):
                if ( IsOutsidePreferenceRange( preference, number ) )
                {
                    Invalid( "The preference is outside its range." );
                }

                return;
            case CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Text or CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Choice
                when value.ValueKind == JsonValueKind.String:
                var text = value.GetString( )!;
                Label( text, preference.MaximumLength, allowEmpty: !preference.Required );

                if ( HasUnknownPreferenceChoice( preference, text ) )
                {
                    Invalid( "The preference choice is invalid." );
                }

                return;
            default:
                Invalid( "The preference value has the wrong type." );

                return;
        }
    }

    public static void ValidatePackagePath( string? path )
    {
        if ( HasInvalidPackagePath( path ) )
        {
            Invalid( "The package path is invalid." );
        }
    }

    public static void ValidateVersion( string? version )
    {
        if ( version is not { Length: > 0 and <= MaximumVersionCharacters } )
        {
            Invalid( "The package version is invalid." );
        }

        var match = VersionPattern( )
            .Match( version! );

        if ( HasInvalidSemVer( match ) )
        {
            Invalid( "The package version must be SemVer." );
        }
    }

    private static void Features( ImmutableArray<string> features )
    {
        if ( HasInvalidFeatures( features ) )
        {
            Invalid( "An extension feature is unsupported or repeated." );
        }
    }

    private static void Label( string? text, int bytes, bool allowEmpty = false )
    {
        if ( HasInvalidLabel( text, bytes, allowEmpty ) )
        {
            Invalid( "A manifest label is invalid or too long." );
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid( string message ) => throw new InvalidDataException( message );

    private static bool HasInvalidPackageId( ExtensionManifest manifest )
    {
        return manifest.Id is not { Length: > 0 and <= MaximumPackageIdCharacters } ||
               !PackageIdPattern( )
                   .IsMatch( manifest.Id );
    }

    private static bool HasUnsupportedProtocol( ExtensionManifest manifest )
    {
        return manifest.Protocol is not { Major: 1, MinimumMinor: 0 } || manifest.Execution?.Profile != RestrictedExecutionProfile;
    }

    private static bool ExceedsContributionLimits( ExtensionManifest manifest )
    {
        return manifest.Targets is not { Count: > 0 and <= 2 } ||
               manifest.Commands.IsDefault ||
               manifest.Commands.Length is < 1 or > ExtensionCommandLimits.MaximumCommands ||
               manifest.Capabilities is null ||
               manifest.Capabilities.Count > MaximumCapabilities ||
               manifest.Preferences.IsDefault ||
               manifest.Preferences.Length > MaximumPreferences;
    }

    private static bool ExceedsSearchProviderLimit( ExtensionManifest manifest )
    {
        return manifest.SearchProviders.IsDefault || manifest.SearchProviders.Length > MaximumSearchProviders;
    }

    private static bool HasInvalidSearchProvider( ExtensionManifest manifest, ExtensionSearchProvider provider )
    {
        return !manifest.Commands.Any( command => command.Id == provider.CommandId ) ||
               provider.SuggestedPrefix is not null &&
               (provider.SuggestedPrefix.Length is < 2 or > MaximumSuggestedPrefixCharacters || !provider.SuggestedPrefix.All( char.IsAsciiLetter ));
    }

    private static bool HasUnsupportedTarget( string rid, ExtensionTarget? target )
    {
        return rid is not (ExtensionTargets.LinuxX64 or ExtensionTargets.LinuxArm64) ||
               target is not { Deployment: ExtensionTargets.NativeAot or ExtensionTargets.SelfContained, RuntimeProfile: RuntimeProfile };
    }

    private static bool HasUnsupportedCapability( string capability, CapabilityRequest? request )
    {
        return !KnownCapabilities.Contains( capability ) || request is null;
    }

    private static bool ExceedsCommandLimits( ExtensionCommand command )
    {
        return command.Keywords.IsDefault ||
               command.Keywords.Length > MaximumKeywords ||
               command.Capabilities.IsDefault ||
               command.Capabilities.Length > MaximumCapabilities;
    }

    private static bool HasInvalidCommandCapabilities( Dictionary<string, CapabilityRequest> capabilities, ExtensionCommand command )
    {
        return command.Capabilities!
                   .Distinct( StringComparer.Ordinal )
                   .Count( ) !=
               command.Capabilities.Length ||
               command.Capabilities.Any( capability => capability is null || !capabilities.ContainsKey( capability ) );
    }

    private static bool HasInvalidPreference( ExtensionPreference preference )
    {
        return preference.Type is not (CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Text
                   or CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Boolean
                   or CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Number
                   or CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Choice
                   or CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Secret) ||
               preference.MaximumLength is < 1 or > MaximumPreferenceBytes ||
               preference.Choices.IsDefault ||
               preference.Choices.Length > MaximumPreferenceChoices ||
               preference.Minimum > preference.Maximum;
    }

    private static bool HasSecretDefault( ExtensionPreference preference )
    {
        return preference is { Type: CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Secret, Default: not null };
    }

    private static bool IsOutsidePreferenceRange( ExtensionPreference preference, double number )
    {
        return number < preference.Minimum || number > preference.Maximum;
    }

    private static bool HasUnknownPreferenceChoice( ExtensionPreference preference, string text )
    {
        return preference.Type == CommandDeck.ExtensionProtocol.ExtensionPreferenceTypes.Choice && !preference.Choices.Any( choice => choice.Value == text );
    }

    private static bool HasInvalidPackagePath( string? path )
    {
        return path is not { Length: > 0 and <= MaximumPackagePathCharacters } ||
               path.Contains( '\\', StringComparison.Ordinal ) ||
               path.Contains( ':', StringComparison.Ordinal ) ||
               path.Any( char.IsControl ) ||
               !path.IsNormalized( NormalizationForm.FormC ) ||
               path.Split( '/' ) is var parts && (parts.Length > MaximumPathSegments || parts.Any( part => part is "" or "." or ".." ));
    }

    private static bool HasInvalidSemVer( Match match )
    {
        return !match.Success ||
               match
                   .Groups[PreReleaseCaptureGroupIndex]
                   .Value.Split( '.' )
                   .Any( part => part.Length > 1 && part[0] == '0' && part.All( char.IsAsciiDigit ) );
    }

    private static bool HasInvalidFeatures( ImmutableArray<string> features )
    {
        return features.IsDefault ||
               features.Length > MaximumRequiredFeatures ||
               features
                   .Distinct( StringComparer.Ordinal )
                   .Count( ) !=
               features.Length ||
               features.Any( feature => feature is null || !KnownFeatures.Contains( feature ) );
    }

    private static bool HasInvalidLabel( string? text, int bytes, bool allowEmpty )
    {
        return text is null || !allowEmpty && string.IsNullOrWhiteSpace( text ) || Encoding.UTF8.GetByteCount( text ) > bytes || text.Any( char.IsControl );
    }
}

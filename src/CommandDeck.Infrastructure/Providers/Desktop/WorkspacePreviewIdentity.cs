using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace CommandDeck;

internal static class WorkspacePreviewIdentity
{
    private const string SpecialWorkspaceName = "special";
    private const string SpecialWorkspacePrefix = SpecialWorkspaceName + ":";
    private const int MinimumSpecialWorkspaceId = -99;
    private const int MaximumSpecialWorkspaceId = -2;

    private const int MaximumIdentityCharacters = 256;

    public static Dictionary<string, string> CreateMetadata( JsonElement workspace )
    {
        Dictionary<string, string> metadata = new( StringComparer.Ordinal );

        if ( CannotReadWorkspaceIdentity( workspace, out var id, out var rawName, out var rawMonitor ) )
        {
            return metadata;
        }

        var name = rawName.GetString( );
        var monitor = rawMonitor.GetString( );
        if ( IsInvalidIdentity( name, monitor ) )
        {
            return metadata;
        }

        // Hyprland reserves -99..-2 for special workspaces; named ordinary
        // workspaces live below that range. Name and range must agree.
        var specialName = name == SpecialWorkspaceName || name.StartsWith( SpecialWorkspacePrefix, StringComparison.Ordinal );
        var specialId = id is >= MinimumSpecialWorkspaceId and <= MaximumSpecialWorkspaceId;
        if ( IsInconsistentSpecialWorkspace( id, specialName, specialId ) )
        {
            return metadata;
        }

        metadata[ResultMetadataKeys.WorkspaceId] = id.ToString( CultureInfo.InvariantCulture );
        metadata[ResultMetadataKeys.WorkspaceName] = name;
        metadata[ResultMetadataKeys.MonitorName] = monitor;
        metadata[ResultMetadataKeys.WorkspaceKind] = specialId ? WorkspaceIdentityKinds.Special : WorkspaceIdentityKinds.Ordinary;

        return metadata;
    }

    private static bool IsInvalidIdentity( [NotNullWhen( false )] string? name, [NotNullWhen( false )] string? monitor )
    {
        return string.IsNullOrWhiteSpace( name ) ||
               string.IsNullOrWhiteSpace( monitor ) ||
               name.Length > MaximumIdentityCharacters ||
               monitor.Length > MaximumIdentityCharacters ||
               name.Any( char.IsControl ) ||
               monitor.Any( char.IsControl );
    }

    private static bool IsInconsistentSpecialWorkspace( int id, bool specialName, bool specialId )
    {
        return id is 0 or -1 || specialName != specialId;
    }

    private static bool CannotReadWorkspaceIdentity( JsonElement workspace, out int id, out JsonElement rawName, out JsonElement rawMonitor )
    {
        id = default;
        rawName = default;
        rawMonitor = default;

        return !workspace.TryGetProperty( "id", out var rawId ) ||
               !int.TryParse( rawId.ToString( ), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id ) ||
               !workspace.TryGetProperty( "name", out rawName ) ||
               rawName.ValueKind != JsonValueKind.String ||
               !workspace.TryGetProperty( "monitor", out rawMonitor ) ||
               rawMonitor.ValueKind != JsonValueKind.String;
    }
}

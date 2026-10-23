namespace CommandDeck;

// A search scoped to one Omarchy submenu lists that menu's entries and nothing
// else, in the order the scoped provider gives them.
public static class OmarchyMenuSearch
{
    public static bool MatchesScope( SearchResult result, string? menuId ) =>
        menuId is null ||
        result is { ProviderId: ProviderIds.Omarchy, Kind: ResultKinds.Omarchy, SourceId: SourceIds.OmarchyControl } &&
        result.Metadata.GetValueOrDefault( ResultMetadataKeys.OmarchyParent ) == menuId;
}

namespace CommandDeck;

internal static class HerdrProjectAction
{
    private const string OpenActionId = "open-herdr";
    private const string ProjectIdKind = "project";
    private const string TrueValue = "true";
    private const string BrowseIdKind = "browse";

    public static CommandAction Create( string path ) => new( )
    {
        Id = OpenActionId,
        Title = "Open in Herdr",
        Kind = ActionKinds.HerdrOpenProject,
        Arguments = new( StringComparer.Ordinal ) { [ActionArgumentNames.Path] = path }
    };

    public static bool Matches( SearchResult result, string path ) =>
        result.Metadata.GetValueOrDefault( ResultMetadataKeys.Path ) == path &&
        (result is { SourceId: SourceIds.Projects, ProviderId: ProviderIds.Projects, Kind: ResultKinds.CustomCommand } &&
         result.Id == StableId.Create( ProjectIdKind, path ) ||
         result is { SourceId: SourceIds.Folders, ProviderId: ProviderIds.Folders, Kind: ResultKinds.File } &&
         result.Metadata.GetValueOrDefault( ResultMetadataKeys.IsDirectory ) == TrueValue &&
         (result.Id == StableId.Create( BrowseIdKind, path ) || result.Id.StartsWith( ResultIdPrefixes.FolderParent, StringComparison.Ordinal )));
}

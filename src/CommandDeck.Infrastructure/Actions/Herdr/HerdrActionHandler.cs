using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

// Herdr focus, project opens, and tab edits. The executable and subcommands
// are host-owned; only the opaque IDs are data, and they must equal the IDs the
// cached result was projected from.
internal sealed class HerdrActionHandler( HerdrClient? herdr, HerdrWindowActivator? herdrWindowActivator ) : ILinuxActionHandler
{
    private const string NameInputArgument = ActionArgumentNames.InputPrefix + "Name";

    private static readonly string[] OwnedKinds =
    [
        ActionKinds.HerdrFocus,
        ActionKinds.HerdrOpenProject,
        ActionKinds.HerdrCreateTab,
        ActionKinds.HerdrRenameTab
    ];

    public IReadOnlyList<string> Kinds => OwnedKinds;

    public async ValueTask<ActionExecutionResult> ExecuteAsync(
        SearchResult result, CommandAction action, string query, CancellationToken cancellationToken ) => action.Kind switch
        {
            ActionKinds.HerdrFocus => await FocusHerdrAsync( result, action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.HerdrOpenProject => await OpenHerdrProjectAsync( result, action, cancellationToken )
                .ConfigureAwait( false ),
            ActionKinds.HerdrCreateTab or ActionKinds.HerdrRenameTab => await EditHerdrTabAsync( result, action, cancellationToken )
                .ConfigureAwait( false ),
            _ => LinuxActionArguments.UnknownKind( action )
        };

    private async ValueTask<ActionExecutionResult> FocusHerdrAsync( SearchResult result, CommandAction action, CancellationToken cancellationToken )
    {
        if ( herdr is null )
        {
            return ActionExecutionResult.Failure( "Herdr is unavailable." );
        }

        var targetKind = LinuxActionArguments.Require( action, ActionArgumentNames.TargetKind );
        var targetId = LinuxActionArguments.Require( action, ActionArgumentNames.TargetId );
        var expectedKind = GetExpectedResultKind( targetKind );
        if ( DoesNotMatchFocusTarget( result, targetKind, targetId, expectedKind ) )
        {
            return ActionExecutionResult.Failure( "This action does not match the selected Herdr result." );
        }

        var activator = herdrWindowActivator;

        var outcome = await herdr
            .FocusAsync( targetKind, targetId, cancellationToken, activator is null ? null : token => activator.ActivateAsync( targetKind, token ) )
            .ConfigureAwait( false );

        if ( !outcome.Succeeded )
        {
            return ActionExecutionResult.Failure( outcome.Message, outcome.Detail );
        }

        return ActionExecutionResult.Success( outcome.Message );
    }

    private async ValueTask<ActionExecutionResult> OpenHerdrProjectAsync( SearchResult result, CommandAction action, CancellationToken cancellationToken )
    {
        if ( herdr is null )
        {
            return ActionExecutionResult.Failure( "Herdr is unavailable." );
        }

        var path = LinuxActionArguments.Require( action, ActionArgumentNames.Path );
        if ( !HerdrProjectAction.Matches( result, path ) )
        {
            return ActionExecutionResult.Failure( "This action does not match the selected project or folder." );
        }

        var activator = herdrWindowActivator;

        var outcome = await herdr
            .OpenProjectAsync( path, cancellationToken, activator is null ? null : token => activator.ActivateAsync( HerdrTargetKinds.Workspace, token ) )
            .ConfigureAwait( false );

        if ( !outcome.Succeeded )
        {
            return ActionExecutionResult.Failure( outcome.Message, outcome.Detail );
        }

        return ActionExecutionResult.Success( outcome.Message );
    }

    private async ValueTask<ActionExecutionResult> EditHerdrTabAsync( SearchResult result, CommandAction action, CancellationToken cancellationToken )
    {
        if ( herdr is null )
        {
            return ActionExecutionResult.Failure( "Herdr is unavailable." );
        }

        var create = action.Kind == ActionKinds.HerdrCreateTab;
        var targetKind = create ? HerdrTargetKinds.Workspace : HerdrTargetKinds.Tab;
        var targetId = LinuxActionArguments.Require( action, ActionArgumentNames.TargetId );
        if ( DoesNotMatchTabTarget( result, create, targetKind, targetId ) )
        {
            return ActionExecutionResult.Failure( "This action does not match the selected Herdr result." );
        }

        var name = LinuxActionArguments.Require( action, NameInputArgument );

        var outcome = await EditTabAsync( herdr, create, targetId, name, cancellationToken )
            .ConfigureAwait( false );

        if ( outcome.Succeeded )
        {
            return ActionExecutionResult.Success( outcome.Message );
        }

        return ActionExecutionResult.Failure( outcome.Message );
    }

    private static bool DoesNotMatchFocusTarget( SearchResult result, string targetKind, string targetId, [NotNullWhen( false )] string? expectedKind )
    {
        return expectedKind is null ||
               result.SourceId != SourceIds.Herdr ||
               result.Kind != expectedKind ||
               !HerdrClient.IsValidIdentifier( targetId ) ||
               result.Metadata.GetValueOrDefault( HerdrProvider.TargetKindMetadata ) != targetKind ||
               result.Metadata.GetValueOrDefault( HerdrProvider.TargetIdMetadata ) != targetId;
    }

    private static bool DoesNotMatchTabTarget( SearchResult result, bool create, string targetKind, string targetId )
    {
        return result.SourceId != SourceIds.Herdr ||
               result.ProviderId != ProviderIds.Herdr ||
               result.Kind != (create ? ResultKinds.Workspace : ResultKinds.Tab) ||
               result.Id != $"{ResultIdPrefixes.Herdr}{targetKind}:{targetId}" ||
               result.Metadata.GetValueOrDefault( HerdrProvider.TargetKindMetadata ) != targetKind ||
               result.Metadata.GetValueOrDefault( HerdrProvider.TargetIdMetadata ) != targetId;
    }

    private static string? GetExpectedResultKind( string targetKind )
    {
        return targetKind switch
        {
            HerdrTargetKinds.Workspace => ResultKinds.Workspace,
            HerdrTargetKinds.Tab => ResultKinds.Tab,
            _ => null
        };
    }

    private static async ValueTask<HerdrTabOutcome> EditTabAsync( HerdrClient herdr, bool create, string targetId, string name,
        CancellationToken cancellationToken )
    {
        if ( create )
        {
            return await herdr
                .CreateTabAsync( targetId, name, cancellationToken )
                .ConfigureAwait( false );
        }

        return await herdr
            .RenameTabAsync( targetId, name, cancellationToken )
            .ConfigureAwait( false );
    }
}

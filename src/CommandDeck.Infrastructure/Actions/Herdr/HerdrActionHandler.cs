using System.Diagnostics.CodeAnalysis;

namespace CommandDeck;

// Herdr focus, project opens, and tab edits. The executable and subcommands
// are host-owned; only the opaque IDs are data, and they must equal the IDs the
// cached result was projected from. The result's target kind picks the
// terminal app (herdr) or the desktop app (herdr-app); project opens use the
// terminal app.
internal sealed class HerdrActionHandler( HerdrClient? herdr, HerdrWindowActivator? herdrWindowActivator, HerdrAppClient? herdrApp = null )
    : ILinuxActionHandler
{
    private const string HerdrNameInput = "Name";
    private const string NameInputArgument = ActionArgumentNames.InputPrefix + HerdrNameInput;

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
        var app = HerdrTargetKinds.IsApp( action.Arguments.GetValueOrDefault( ActionArgumentNames.TargetKind ) );
        if ( Unavailable( app ) is { } unavailable )
        {
            return unavailable;
        }

        var targetKind = LinuxActionArguments.Require( action, ActionArgumentNames.TargetKind );
        var targetId = LinuxActionArguments.Require( action, ActionArgumentNames.TargetId );
        var expectedKind = GetExpectedResultKind( targetKind );
        if ( DoesNotMatchFocusTarget( result, targetKind, targetId, expectedKind ) )
        {
            return ActionExecutionResult.Failure( "This action does not match the selected Herdr result." );
        }

        var activator = herdrWindowActivator;

        var outcome = app
            ? await herdrApp!
                .FocusAsync( targetKind, targetId, cancellationToken,
                    activator is null ? null : ( listener, token ) => activator.ActivateAppAsync( targetKind, listener, token ) )
                .ConfigureAwait( false )
            : await herdr!
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
        var app = HerdrTargetKinds.IsApp( result.Metadata.GetValueOrDefault( HerdrProvider.TargetKindMetadata ) );
        if ( Unavailable( app ) is { } unavailable )
        {
            return unavailable;
        }

        var create = action.Kind == ActionKinds.HerdrCreateTab;
        var targetKind = create ? (app ? HerdrTargetKinds.AppSpace : HerdrTargetKinds.Workspace) : (app ? HerdrTargetKinds.AppTab : HerdrTargetKinds.Tab);
        var targetId = LinuxActionArguments.Require( action, ActionArgumentNames.TargetId );
        if ( DoesNotMatchTabTarget( result, create, targetKind, targetId ) )
        {
            return ActionExecutionResult.Failure( "This action does not match the selected Herdr result." );
        }

        var name = LinuxActionArguments.Require( action, NameInputArgument );

        // The dialog opens with the desktop tab's shown name, which may be its
        // automatic number or a shortened long name; submitting it unchanged,
        // give or take surrounding spaces Herdr trims, must not store that as
        // the tab's own name. An invalid name goes on to the client's error.
        if ( app && !create && HerdrAppClient.IsValidTabName( name ) && IsUnchangedName( result, action, name ) )
        {
            return ActionExecutionResult.Success( "Herdr tab name unchanged." );
        }

        var outcome = app
            ? await EditAppTabAsync( herdrApp!, create, targetId, name, cancellationToken )
                .ConfigureAwait( false )
            : await EditTabAsync( herdr!, create, targetId, name, cancellationToken )
                .ConfigureAwait( false );

        if ( outcome.Succeeded )
        {
            return ActionExecutionResult.Success( outcome.Message );
        }

        return ActionExecutionResult.Failure( outcome.Message );
    }

    private static bool IsUnchangedName( SearchResult result, CommandAction action, string name ) =>
        result
            .Actions.FirstOrDefault( cached => cached.Id == action.Id && cached.Kind == action.Kind )?
            .Inputs.FirstOrDefault( static input => input.Name == HerdrNameInput )?
            .DefaultValue is { } shown && string.Equals( shown.Trim( ), name.Trim( ), StringComparison.Ordinal );

    private static bool DoesNotMatchFocusTarget( SearchResult result, string targetKind, string targetId, [NotNullWhen( false )] string? expectedKind )
    {
        return expectedKind is null ||
               result.SourceId != SourceIds.Herdr ||
               result.Kind != expectedKind ||
               !HerdrClient.IsValidIdentifier( targetId ) ||
               targetKind == HerdrTargetKinds.AppPane && !HerdrSearch.IsAgentRow( result ) ||
               result.Metadata.GetValueOrDefault( HerdrProvider.TargetKindMetadata ) != targetKind ||
               result.Metadata.GetValueOrDefault( HerdrProvider.TargetIdMetadata ) != targetId;
    }

    private static bool DoesNotMatchTabTarget( SearchResult result, bool create, string targetKind, string targetId )
    {
        // A space creates tabs from its own row or from its "Create tab" row,
        // whose ID carries the browsed space's scope.
        var expectedId = $"{ResultIdPrefixes.Herdr}{targetKind}:{targetId}";
        var offerId = HerdrSearch.NewTabResultPrefix + HerdrScopes.Of( HerdrTargetKinds.IsApp( targetKind ), targetId );

        return result.SourceId != SourceIds.Herdr ||
               result.ProviderId != ProviderIds.Herdr ||
               result.Kind != (create ? ResultKinds.Workspace : ResultKinds.Tab) ||
               result.Id != expectedId && !(create && result.Id == offerId) ||
               result.Metadata.GetValueOrDefault( HerdrProvider.TargetKindMetadata ) != targetKind ||
               result.Metadata.GetValueOrDefault( HerdrProvider.TargetIdMetadata ) != targetId;
    }

    private static string? GetExpectedResultKind( string targetKind )
    {
        return targetKind switch
        {
            HerdrTargetKinds.Workspace or HerdrTargetKinds.AppSpace => ResultKinds.Workspace,
            HerdrTargetKinds.Tab or HerdrTargetKinds.AppTab or HerdrTargetKinds.AppPane => ResultKinds.Tab,
            _ => null
        };
    }

    private ActionExecutionResult? Unavailable( bool app )
    {
        if ( app ? herdrApp is null : herdr is null )
        {
            return ActionExecutionResult.Failure( app ? "The Herdr app is unavailable." : "Herdr is unavailable." );
        }

        return null;
    }

    private static async ValueTask<HerdrTabOutcome> EditAppTabAsync( HerdrAppClient app, bool create, string targetId, string name,
        CancellationToken cancellationToken )
    {
        if ( create )
        {
            return await app
                .CreateTabAsync( targetId, name, cancellationToken )
                .ConfigureAwait( false );
        }

        return await app
            .RenameTabAsync( targetId, name, cancellationToken )
            .ConfigureAwait( false );
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

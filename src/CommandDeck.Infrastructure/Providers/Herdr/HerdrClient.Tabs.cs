using System.Text.Json;

namespace CommandDeck;

public sealed record HerdrTabOutcome( bool Succeeded, string Message );

public sealed partial class HerdrClient
{
    public ValueTask<HerdrTabOutcome> CreateTabAsync( string workspaceId, string name, CancellationToken cancellationToken ) =>
        MutateTabAsync( workspaceId, name, create: true, cancellationToken );

    public ValueTask<HerdrTabOutcome> RenameTabAsync( string tabId, string name, CancellationToken cancellationToken ) =>
        MutateTabAsync( tabId, name, create: false, cancellationToken );

    private async ValueTask<HerdrTabOutcome> MutateTabAsync( string targetId, string name, bool create, CancellationToken cancellationToken )
    {
        cancellationToken.ThrowIfCancellationRequested( );

        if ( IsInvalidTabName( targetId, name ) )
        {
            return new( false, "Choose a valid Herdr target and a name of 1–512 characters without control characters." );
        }

        if ( !_available( ) )
        {
            return new( false, "Herdr is not on the daemon PATH." );
        }

        string[] arguments = CreateTabArguments( targetId, name, create );
        ProcessResult result;

        try
        {
            result = await RunAsync( arguments, FocusTimeout, FocusStandardOutputBytes, FocusStandardErrorBytes, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( Exception exception ) when ( exception is IOException or InvalidOperationException )
        {
            return new( false, "The Herdr client could not start." );
        }

        // A lost acknowledgement must never cause an automatic second creation.
        if ( HasTabRequestExceededLimits( result ) )
        {
            return new( false, "Herdr did not confirm the change; it may already have happened. Refresh before trying again." );
        }

        if ( result.ExitCode != 0 )
        {
            return new( false, "Herdr could not change this tab. Refresh to check that the space or tab still exists." );
        }

        return VerifyTabMutation( result.StandardOutput, targetId, name, create );
    }

    private static HerdrTabOutcome VerifyTabMutation( string json, string targetId, string name, bool create )
    {
        const string unconfirmed = "Herdr did not confirm the requested change. Refresh before trying again.";

        try
        {
            using var document = JsonDocument.Parse( json );
            var envelope = document.RootElement;
            if ( CannotReadConfirmedTab( create, envelope, out var tab ) )
            {
                return new( false, unconfirmed );
            }

            var tabId = ReadString( tab, TabIdField );
            var confirmedTarget = ReadString( tab, create ? WorkspaceIdField : TabIdField );
            if ( IsInvalidTabConfirmation( targetId, name, tab, tabId, confirmedTarget ) )
            {
                return new( false, unconfirmed );
            }

            return new( true, create ? "Created Herdr tab." : "Renamed Herdr tab." );
        }
        catch ( JsonException )
        {
            return new( false, unconfirmed );
        }
    }

    private static bool IsInvalidTabName( string targetId, string name )
    {
        return !IsValidIdentifier( targetId ) || string.IsNullOrWhiteSpace( name ) || name.Length > MaximumLabelCharacters || name.Any( char.IsControl );
    }

    private static string[] CreateTabArguments( string targetId, string name, bool create )
    {
        if ( create )
        {
            return [TabCommand, CreateCommand, WorkspaceOption, targetId, LabelOption, name, NoFocusOption];
        }

        return [TabCommand, RenameCommand, targetId, name];
    }

    private static bool HasTabRequestExceededLimits( ProcessResult result )
    {
        return result.TimedOut || result.AnyOutputLimitExceeded || result.MemoryLimitExceeded;
    }

    private static bool IsInvalidTabConfirmation( string targetId, string name, JsonElement tab, string? tabId, string? confirmedTarget )
    {
        return tabId is null || !IsValidIdentifier( tabId ) || confirmedTarget != targetId || ReadString( tab, "label" ) != name;
    }

    private static bool CannotReadConfirmedTab( bool create, JsonElement envelope, out JsonElement tab )
    {
        tab = default;

        return envelope.ValueKind != JsonValueKind.Object ||
               envelope.TryGetProperty( "error", out _ ) ||
               !envelope.TryGetProperty( "result", out var payload ) ||
               payload.ValueKind != JsonValueKind.Object ||
               ReadString( payload, "type" ) != (create ? "tab_created" : TabInfoType) ||
               !payload.TryGetProperty( "tab", out tab ) ||
               tab.ValueKind != JsonValueKind.Object;
    }
}

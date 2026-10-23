using System.Text.Json.Nodes;

namespace CommandDeck;

public sealed partial class AgentConversationCoordinator
{
    private const int PackageSearchResultLimit = 12;
    private const int PackagePreviewLimit = 24;
    private const int MaximumPackageCommitPolls = 1800;
    private const int PackageCommitPollMilliseconds = 1000;

    private async ValueTask<AgentToolResult> CommandToolAsync( Conversation conversation, JsonObject arguments, CancellationToken cancellationToken )
    {
        List<string> argv = [];
        if ( arguments[AgentToolFields.Argv] is JsonArray array )
        {
            foreach ( var node in array )
            {
                argv.Add( ( string? ) node ?? string.Empty );
            }
        }

        var plan = _commands.Plan( argv, ReadOptional( arguments, "cwd" ) );

        var remembered = await ReadRememberedCommandApprovalAsync( plan, cancellationToken )
            .ConfigureAwait( false );

        // Skip the per-command card for a remembered command, or — only when the
        // option is on — for a direct read-only tool (shells and writers never
        // qualify). Otherwise every command is confirmed, as before.
        var autoApproved = CanAutoApproveCommand( plan );
        if ( !remembered && !autoApproved )
        {
            if ( !await ConfirmCommandAsync( conversation, plan, ReadOptional( arguments, AgentToolFields.Rationale ), cancellationToken )
                    .ConfigureAwait( false ) )
            {
                return AgentToolResult.Denied( "The user declined the command." );
            }
        }

        var outcome = await AgentCommandTool
            .ExecuteAsync( plan, cancellationToken )
            .ConfigureAwait( false );

        JsonObject payload = new( )
        {
            ["exitCode"] = outcome.ExitCode,
            ["timedOut"] = outcome.TimedOut,
            [AgentToolFields.Truncated] = outcome.Truncated,
            ["output"] = outcome.Output
        };

        return AgentToolResult.Ok( payload.ToJsonString( ) );
    }

    private async ValueTask<bool> ConfirmCommandAsync( Conversation conversation, AgentCommandPlan plan, string? rationale,
        CancellationToken cancellationToken )
    {
        AgentActionCard card = new( )
        {
            Id = NewId( ),
            Kind = AgentActionDraftKinds.Command,
            Title = "Run command: " + Path.GetFileName( plan.Executable ),
            Risk = AgentActionRisks.Confirm,
            Executable = plan.Executable,
            Argv = [.. plan.Argv],
            WorkingDirectory = plan.WorkingDirectory,
            EnvironmentProfile = plan.EnvironmentProfile,
            Environment = new Dictionary<string, string>( plan.Environment, StringComparer.Ordinal ),
            CanRemember = true,
            Details =
            [
                new DetailField( "Executable", plan.Executable, Monospace: true ),
                new DetailField( "Working directory", plan.WorkingDirectory, Monospace: true ),
                new DetailField( "Environment", string.Join( ' ', plan
                    .Environment.OrderBy( static pair => pair.Key )
                    .Select( static pair => $"{pair.Key}={pair.Value}" ) ), Monospace: true ),
                new DetailField( "Timeout", $"{AgentLimits.MaximumCommandSeconds} seconds" ),
                new DetailField( "Output", "Bounded output returns to the model" )
            ],
            Rationale = rationale
        };

        var decision = await ParkAsync( conversation, new AgentPendingRequest
        {
            Id = card.Id,
            Kind = AgentPendingRequestKinds.Command,
            TurnId = conversation.ActiveTurnId,
            Prompt = card.Title,
            Action = card,
            Decisions =
                [
                    AgentRequestDecisions.Allow,
                    AgentRequestDecisions.AllowAlways,
                    AgentRequestDecisions.Deny,
                    AgentRequestDecisions.CancelTurn
                ]
        }, cancellationToken )
            .ConfigureAwait( false );

        if ( decision.Decision is not (AgentRequestDecisions.Allow or AgentRequestDecisions.AllowAlways) )
        {
            return false;
        }

        if ( decision.Decision == AgentRequestDecisions.AllowAlways )
        {
            await RememberCommandAsync( plan, cancellationToken )
                .ConfigureAwait( false );
        }

        return true;
    }

    private async ValueTask<bool> ReadRememberedCommandApprovalAsync( AgentCommandPlan plan, CancellationToken cancellationToken )
    {
        var grants = await _authority
            .GetGrantsAsync( cancellationToken )
            .ConfigureAwait( false );

        return grants.Any( grant =>
            grant is { Kind: AgentGrantKinds.Command, State: AgentGrantStates.Active } &&
            string.Equals( grant.PayloadDigest, plan.Digest, StringComparison.Ordinal ) );
    }

    private async ValueTask RememberCommandAsync( AgentCommandPlan plan, CancellationToken cancellationToken )
    {
        await _authority
            .AddGrantAsync( new AgentGrant
            {
                Id = NewId( ),
                Kind = AgentGrantKinds.Command,
                Scope = string.Join( ' ', plan.Argv ),
                Identity = plan.ExecutableIdentity,
                PayloadDigest = plan.Digest,
                State = AgentGrantStates.Active,
                Label = Path.GetFileName( plan.Executable )
            }, cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask<AgentToolResult> PackageSearchToolAsync( JsonObject arguments, CancellationToken cancellationToken )
    {
        var result = await _packages!
            .SearchAsync( ReadString( arguments, AgentToolFields.Query ), PackageSearchResultLimit, cancellationToken )
            .ConfigureAwait( false );

        if ( result.Error is not null )
        {
            return AgentToolResult.Denied( result.Error );
        }

        JsonArray packages = new( );
        foreach ( var record in result.Packages )
        {
            JsonNode packageNode = new JsonObject
            {
                [AgentToolFields.Name] = record.Name,
                ["repository"] = record.Repository,
                ["version"] = record.Version,
                ["description"] = record.Description,
                ["installed"] = record.Installed
            };

            packages.Add( packageNode );
        }

        return AgentToolResult.Ok( new JsonObject { [AgentToolFields.Packages] = packages, [AgentToolFields.Truncated] = result.Truncated }.ToJsonString( ) );
    }

    private async ValueTask<AgentToolResult> PackageChangeToolAsync( Conversation conversation, JsonObject arguments, CancellationToken cancellationToken )
    {
        var operation = ReadString( arguments, "operation" );
        var names = ReadPackageNames( arguments );

        var preview = await _packages!
            .PreviewAsync( operation, names, ReadOptional( arguments, AgentToolFields.Rationale ), cancellationToken )
            .ConfigureAwait( false );

        if ( !preview.Commitable )
        {
            return AgentToolResult.Denied( preview.Reason ?? "The package change cannot be previewed." );
        }

        var helper = await ReadPackageHelperStatusAsync( cancellationToken )
            .ConfigureAwait( false );

        if ( !helper.Installed )
        {
            return AgentToolResult.Denied( "The privileged package helper is not installed, so package changes are unavailable." );
        }

        var card = BuildPackageCard( preview );

        var decision = await ParkAsync( conversation,
                new AgentPendingRequest
                {
                    Id = card.Id,
                    Kind = AgentPendingRequestKinds.Action,
                    TurnId = conversation.ActiveTurnId,
                    Prompt = card.Title,
                    Detail = string.Join( " ", preview.Warnings ),
                    Action = card,
                    Decisions = [AgentRequestDecisions.Allow, AgentRequestDecisions.Deny, AgentRequestDecisions.CancelTurn]
                }, cancellationToken )
            .ConfigureAwait( false );

        if ( decision.Decision != AgentRequestDecisions.Allow )
        {
            return AgentToolResult.Denied( "The user declined the package change." );
        }

        var commit = await CommitPackageChangeAsync( preview, cancellationToken, conversation.AuthorityEpoch )
            .ConfigureAwait( false );

        JsonObject payload = new( ) { ["operationId"] = commit.OperationId, ["state"] = commit.State, [AgentToolFields.Message] = commit.Message };
        if ( commit.State == PackageOperationStates.Completed )
        {
            return AgentToolResult.Ok( payload.ToJsonString( ) );
        }
        else
        {
            return AgentToolResult.Denied( payload.ToJsonString( ) );
        }
    }

    private static AgentActionCard BuildPackageCard( PackageTransactionPreview preview )
    {
        List<DetailField> details = [];
        foreach ( var entry in preview.Entries.Take( PackagePreviewLimit ) )
        {
            var version = entry.TargetVersion ?? entry.CurrentVersion ?? string.Empty;

            details.Add( new DetailField( entry.Requested ? entry.Name : entry.Name + " (dependency)", version, Monospace: true ) );
        }

        details.Add( new DetailField( "Download", $"{preview.DownloadBytes / ByteUnits.BytesPerKibibyte} KiB" ) );
        details.Add( new DetailField( "Installed size change", $"{preview.InstalledDeltaBytes / ByteUnits.BytesPerKibibyte} KiB" ) );

        return new AgentActionCard
        {
            Id = preview.PreviewId,
            Kind = AgentActionDraftKinds.PackageChange,
            Title =
                (preview.Operation == PackageOperations.Remove ? "Remove packages: " : "Install packages: ") + string.Join( ", ", preview.RequestedNames ),
            Risk = AgentActionRisks.Confirm,
            Argv = [.. preview.Route],
            Destructive = preview.Destructive,
            CanRemember = false,
            Details = details,
            Rationale = preview.Rationale
        };
    }

    public async ValueTask<PackageCommitResult> CommitPackageChangeAsync( PackageTransactionPreview preview, CancellationToken cancellationToken,
        long? expectedEpoch = null )
    {
        ArgumentNullException.ThrowIfNull( preview );

        // Inside a turn, enforce the lease without waiting for that same turn
        // to stop. Both UI and tool commits re-read persisted authority here.
        var preferences = await EnforceLeaseAsync( cancellationToken )
            .ConfigureAwait( false );

        if ( !preferences.Enabled || preferences.TrustLevel < AgentTrustLevels.Packages )
        {
            throw new InvalidOperationException( "Package changes require trust level 4." );
        }

        if ( expectedEpoch is { } epoch && !_authority.IsCurrent( epoch ) )
        {
            throw new InvalidOperationException( "Assistant permissions changed; preview the package change again." );
        }

        if ( _helper is null )
        {
            return new PackageCommitResult
            {
                OperationId = preview.PreviewId,
                State = PackageOperationStates.Failed,
                Message = "The package helper is not installed."
            };
        }

        var title = preview.Operation + " " + string.Join( ", ", preview.RequestedNames );
        string operationId;

        try
        {
            operationId = await _helper
                .CommitAsync( preview.Operation, preview.RequestedNames, preview.DatabaseIdentity, preview.TransactionDigest, cancellationToken )
                .ConfigureAwait( false );
        }
        catch ( InvalidOperationException exception )
        {
            return new PackageCommitResult { OperationId = preview.PreviewId, State = PackageOperationStates.Failed, Message = Sanitize( exception.Message ) };
        }

        RecordOperation( operationId, PackageOperationStates.Running, title, "Authorizing and running pacman." );

        return await WaitForPackageCommitAsync( operationId, title, cancellationToken )
            .ConfigureAwait( false );
    }

    private async ValueTask<AgentHelperStatus> ReadPackageHelperStatusAsync( CancellationToken cancellationToken )
    {
        AgentHelperStatus? helper;
        if ( _helper is null )
        {
            helper = new AgentHelperStatus { Installed = false, Message = "The package helper is not installed." };
        }
        else
        {
            helper = await _helper
                .GetStatusAsync( cancellationToken )
                .ConfigureAwait( false );
        }

        return helper;
    }

    private async ValueTask<PackageCommitResult> WaitForPackageCommitAsync( string operationId, string title, CancellationToken cancellationToken )
    {
        var result = await _helper!
            .GetOperationAsync( operationId, cancellationToken )
            .ConfigureAwait( false );

        var polls = 0;
        while ( result.State is PackageOperationStates.Queued
                    or PackageOperationStates.Authorizing
                    or PackageOperationStates.Running
                    or PackageOperationStates.Verifying &&
                polls++ < MaximumPackageCommitPolls )
        {
            await Task
                .Delay( PackageCommitPollMilliseconds, cancellationToken )
                .ConfigureAwait( false );

            result = await _helper!
                .GetOperationAsync( operationId, cancellationToken )
                .ConfigureAwait( false );

            RecordOperation( operationId, result.State, title, result.Message );
        }

        RecordOperation( operationId, result.State, title, result.Message );

        return result;
    }

    private static List<string> ReadPackageNames( JsonObject arguments )
    {
        List<string> names = [];
        if ( arguments[AgentToolFields.Packages] is JsonArray array )
        {
            foreach ( var node in array )
            {
                names.Add( ( string? ) node ?? string.Empty );
            }
        }

        return names;
    }

    private bool CanAutoApproveCommand( AgentCommandPlan plan )
    {
        return _autoApproveReadOnlyCommands && AgentLauncherTools.ClassifyCommandRisk( plan ) == AgentActionRisks.Immediate;
    }
}

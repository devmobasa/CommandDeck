using System.Text.Json;

namespace CommandDeck;

public static partial class Program
{
    private const int DefaultCatalogWorkItems = 50;
    private const int MaximumCatalogWorkItems = 256;

    private static async Task<int> RunCatalogAsync( CommandDeckRuntime runtime, CliArguments arguments, CancellationToken cancellationToken )
    {
        var positionals = arguments.PositionalsAfterCommand( );
        var action = ReadCliAction( positionals, 0, CatalogOperationNames.Inspect );
        if ( action == CatalogOperationNames.Inspect )
        {
            var inspection = await runtime
                .CatalogOperations.InspectAsync( ParseInteger( arguments.GetOption( CliOptionNames.Offset ), 0, 0, int.MaxValue ),
                    ParseInteger( arguments.GetOption( CliOptionNames.Limit ), DefaultCatalogWorkItems, 1, MaximumCatalogWorkItems ), cancellationToken )
                .ConfigureAwait( false );

            if ( arguments.HasFlag( CliOptionNames.Json ) )
            {
                Console.WriteLine( JsonSerializer.Serialize( inspection, CommandDeckJsonContext.Default.CatalogInspection ) );

                return 0;
            }

            PrintCatalogInspection( inspection );

            return 0;
        }

        if ( action == CatalogOperationNames.Audit )
        {
            var audit = await runtime
                .CatalogOperations.AuditPrivacyAsync( cancellationToken )
                .ConfigureAwait( false );

            if ( arguments.HasFlag( CliOptionNames.Json ) )
            {
                Console.WriteLine( JsonSerializer.Serialize( audit, CommandDeckJsonContext.Default.PrivacyAuditResult ) );
            }
            else
            {
                Console.WriteLine( DescribePrivacyAudit( audit ) );

                foreach ( var message in audit.Messages )
                {
                    Console.WriteLine( $"- {message}" );
                }
            }

            return audit.Succeeded ? 0 : 2;
        }

        if ( action == CatalogOperationNames.Purge && !arguments.HasFlag( CliOptionNames.Yes ) )
        {
            Console.Error.WriteLine( $"catalog {CatalogOperationNames.Purge} requires {CliOptionNames.Yes}." );

            return 2;
        }

        if ( action is not (CatalogOperationNames.Integrity or CatalogOperationNames.Optimize or CatalogOperationNames.Repair or CatalogOperationNames.Purge) )
        {
            Console.Error.WriteLine( $"Use 'catalog {CatalogOperationNames.Inspect}', 'catalog {CatalogOperationNames.Audit}', " +
                                     $"'catalog {CatalogOperationNames.Integrity}', 'catalog {CatalogOperationNames.Optimize}', " +
                                     $"'catalog {CatalogOperationNames.Repair}', or 'catalog {CatalogOperationNames.Purge} {CliOptionNames.Yes}'." );

            return 2;
        }

        var result = action switch
        {
            CatalogOperationNames.Integrity => await runtime
                .CatalogOperations.CheckIntegrityAsync( cancellationToken )
                .ConfigureAwait( false ),
            CatalogOperationNames.Optimize => await runtime
                .CatalogOperations.OptimizeAsync( cancellationToken )
                .ConfigureAwait( false ),
            CatalogOperationNames.Repair => await runtime
                .CatalogOperations.RepairAsync( cancellationToken )
                .ConfigureAwait( false ),
            CatalogOperationNames.Purge => await runtime
                .CatalogOperations.PurgeProjectionAsync( cancellationToken )
                .ConfigureAwait( false ),
            _ => throw new InvalidOperationException( "Unreachable catalog action." )
        };

        if ( arguments.HasFlag( CliOptionNames.Json ) )
        {
            Console.WriteLine( JsonSerializer.Serialize( result, CommandDeckJsonContext.Default.CatalogMaintenanceResult ) );
        }
        else
        {
            Console.WriteLine( DescribeCatalogMaintenance( result ) );

            foreach ( var message in result.Messages )
            {
                Console.WriteLine( $"- {message}" );
            }
        }

        return result.Succeeded ? 0 : 2;
    }

    private static void PrintCatalogInspection( CatalogInspection inspection )
    {
        Console.WriteLine( $"Items:            {inspection.Stats.ItemCount}" );
        Console.WriteLine( $"Database:         {inspection.Stats.DatabaseBytes} bytes" );
        Console.WriteLine( $"Rebuild required: {(inspection.RebuildRequired ? "yes" : "no")}" );
        Console.WriteLine( "Sources:" );

        foreach ( var source in inspection.SourceCounts )
        {
            Console.WriteLine( $"  {source.Id}: {source.Count}" );
        }

        if ( inspection.SourceCountsTruncated )
        {
            Console.WriteLine( "  (additional source counts omitted)" );
        }

        Console.WriteLine( "Kinds:" );

        foreach ( var kind in inspection.KindCounts )
        {
            Console.WriteLine( $"  {kind.Id}: {kind.Count}" );
        }

        if ( inspection.KindCountsTruncated )
        {
            Console.WriteLine( "  (additional kind counts omitted)" );
        }

        if ( !inspection.DetailedWorkStateAvailable )
        {
            Console.WriteLine( "Per-item failed/stale/pending state: not tracked by full-scan indexing" );
        }
        else
        {
            var countPrefix = inspection.WorkItemCountsExact ? string.Empty : "at least ";

            Console.WriteLine( $"Work:             failed={countPrefix}{inspection.FailedItemCount} " +
                               $"stale={countPrefix}{inspection.StaleItemCount} " +
                               $"pending={countPrefix}{inspection.PendingItemCount}" );

            foreach ( var item in inspection.WorkItems )
            {
                Console.WriteLine( $"  {PreferencesTextFormatter.EscapeTerminalText( item.State )} · " +
                                   $"{PreferencesTextFormatter.EscapeTerminalText( item.Root )}: " +
                                   $"{PreferencesTextFormatter.EscapeTerminalText( item.RelativePath )} — " +
                                   PreferencesTextFormatter.EscapeTerminalText( item.Reason ) );
            }

            if ( inspection.WorkItemsTruncated )
            {
                Console.WriteLine( DescribeRemainingWorkItems( inspection ) );
            }
        }

        Console.WriteLine( $"Exclusions:       about {inspection.TotalExclusionDiagnosticCount}" );

        foreach ( var diagnostic in inspection.ExclusionDiagnostics )
        {
            string? rule;
            if ( string.IsNullOrWhiteSpace( diagnostic.Rule ) )
            {
                rule = string.Empty;
            }
            else
            {
                rule = $" ({PreferencesTextFormatter.EscapeTerminalText( diagnostic.Rule )})";
            }

            Console.WriteLine( $"  {PreferencesTextFormatter.EscapeTerminalText( diagnostic.Root )}: " +
                               $"{PreferencesTextFormatter.EscapeTerminalText( diagnostic.RelativePath )} — " +
                               $"{PreferencesTextFormatter.EscapeTerminalText( diagnostic.Reason )}{rule}" );
        }

        if ( inspection.ExclusionDiagnosticsTruncated )
        {
            Console.WriteLine( "  (additional exclusion diagnostics omitted)" );
        }
    }

    private static string DescribePrivacyAudit( PrivacyAuditResult audit )
    {
        if ( audit.Succeeded )
        {
            return "Privacy audit passed.";
        }
        else
        {
            return "Privacy audit found violations.";
        }
    }

    private static string DescribeCatalogMaintenance( CatalogMaintenanceResult result )
    {
        if ( result.Succeeded )
        {
            return $"Catalog {result.Operation} completed.";
        }
        else
        {
            return $"Catalog {result.Operation} found problems.";
        }
    }

    private static string DescribeRemainingWorkItems( CatalogInspection inspection )
    {
        if ( inspection.WorkItemCountsExact )
        {
            return $"  (more work items available after offset {inspection.WorkItemOffset + inspection.WorkItems.Count})";
        }
        else
        {
            return "  (work-item sample is incomplete; counts are lower bounds)";
        }
    }
}

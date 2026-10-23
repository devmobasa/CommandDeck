using System.Globalization;
using System.Text;

namespace CommandDeck;

internal static class HyprlandDispatch
{
    internal const string Command = "dispatch";
    internal const string AcceptedOutput = "ok";
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds( 3 );

    internal static bool WasAccepted( ProcessResult result ) =>
        result is { ExitCode: 0, TimedOut: false, AnyOutputLimitExceeded: false } &&
        string.Equals( result.StandardOutput.Trim( ), AcceptedOutput, StringComparison.Ordinal );

    // A dispatch that was not acknowledged, including a Lua warning printed with
    // exit code zero, with Hyprland's own text as the detail.
    internal static ActionExecutionResult Failure( ProcessResult result )
    {
        string message;

        if ( result.TimedOut )
        {
            message = "Hyprland dispatch timed out.";
        }
        else
        {
            if ( result.ExitCode == 0 )
            {
                message = "Hyprland did not acknowledge the action.";
            }
            else
            {
                message = $"'hyprctl' exited with code {result.ExitCode.ToString( CultureInfo.InvariantCulture )}.";
            }
        }

        string diagnostic;

        if ( string.IsNullOrWhiteSpace( result.StandardError ) )
        {
            diagnostic = result.StandardOutput.Trim( );
        }
        else
        {
            diagnostic = result.StandardError.Trim( );
        }

        return ActionExecutionResult.Failure( message, FormatDiagnostic( diagnostic ) );
    }

    internal static string QuoteLuaString( string value )
    {
        StringBuilder quoted = new( "\"" );

        foreach ( var character in value )
        {
            if ( character is '"' or '\\' )
            {
                quoted
                    .Append( '\\' )
                    .Append( character );
            }
            else if ( IsControlCharacter( character ) )
            {
                quoted
                    .Append( '\\' )
                    .Append( (( int ) character).ToString( "D3", CultureInfo.InvariantCulture ) );
            }
            else
            {
                quoted.Append( character );
            }
        }

        return quoted
            .Append( '"' )
            .ToString( );
    }

    private static bool IsControlCharacter( char character )
    {
        return character is < ' ' or '\u007f';
    }

    private static string? FormatDiagnostic( string diagnostic )
    {
        if ( string.IsNullOrWhiteSpace( diagnostic ) )
        {
            return null;
        }

        return $"```\n{diagnostic}\n```";
    }
}

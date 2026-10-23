using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommandDeck;

public sealed class CodexCredentialStore
{
    private const string ApiKeyProperty = "OPENAI_API_KEY";
    public const int MaximumBytes = 64 * ByteUnits.BytesPerKibibyte;
    internal const string AuthFileName = "auth.json";

    public CodexCredentialStore( string stateDirectory )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace( stateDirectory );

        Directory = Path.Combine( PathExpansion.Expand( stateDirectory ), "codex" );
    }

    public string Directory { get; }

    public string FilePath => Path.Combine( Directory, AuthFileName );

    public bool Exists => File.Exists( FilePath );

    public byte[]? Read( )
    {
        if ( !System.IO.Directory.Exists( Directory ) )
        {
            return null;
        }

        SafeRegularFile file;

        try
        {
            file = SafeRegularFile.OpenFromDirectory( Directory, AuthFileName );
        }
        catch ( InvalidOperationException )
        {
            return null;
        }

        using ( file )
        {
            if ( file.Length is 0 or > MaximumBytes )
            {
                return null;
            }

            var buffer = new byte[file.Length];
            var total = 0;
            while ( total < buffer.Length )
            {
                var read = RandomAccess.Read( file.Handle, buffer.AsSpan( total ), total );
                if ( read <= 0 )
                {
                    break;
                }

                total += read;
            }

            if ( IsInvalidCredentialBuffer( buffer, total ) )
            {
                Clear( buffer );

                return null;
            }

            return buffer;
        }
    }

    public void Write( ReadOnlySpan<byte> json )
    {
        var validation = json.ToArray( );
        bool valid;

        try
        {
            valid = json.Length is > 0 and <= MaximumBytes && IsValidCredentialJson( validation );
        }
        finally
        {
            Clear( validation );
        }

        if ( !valid )
        {
            throw new InvalidOperationException( "The Codex credential is not a valid credential document." );
        }

        System.IO.Directory.CreateDirectory( Directory );

        if ( OperatingSystem.IsLinux( ) )
        {
            File.SetUnixFileMode( Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
        }

        var temporary = Path.Combine( Directory, $".auth-{Guid.NewGuid( ):N}.tmp" );

        try
        {
            using ( var stream = SafeRegularFile.CreatePrivateWriteStream( temporary ) )
            {
                stream.Write( json );
                stream.Flush( flushToDisk: true );
            }

            File.Move( temporary, FilePath, overwrite: true );
        }
        finally
        {
            if ( File.Exists( temporary ) )
            {
                File.Delete( temporary );
            }
        }
    }

    public void Delete( )
    {
        if ( File.Exists( FilePath ) )
        {
            File.Delete( FilePath );
        }
    }

    public static bool IsValidCredentialJson( byte[] bytes )
    {
        try
        {
            _ = new UTF8Encoding( false, true ).GetCharCount( bytes );
            using var document = JsonDocument.Parse( bytes );
            if ( document.RootElement.ValueKind != JsonValueKind.Object )
            {
                return false;
            }

            var hasTokens = HasTokenObject( document );
            var hasApiKey = HasApiKeyString( document );

            return hasTokens || hasApiKey;
        }
        catch ( Exception exception ) when ( exception is JsonException or DecoderFallbackException )
        {
            return false;
        }
    }

    internal static void Clear( byte[]? buffer )
    {
        if ( buffer is not null )
        {
            CryptographicOperations.ZeroMemory( buffer );
        }
    }

    private static bool IsInvalidCredentialBuffer( byte[] buffer, int total )
    {
        return total != buffer.Length || !IsValidCredentialJson( buffer );
    }

    private static bool HasTokenObject( JsonDocument document )
    {
        return document.RootElement.TryGetProperty( "tokens", out var tokens ) && tokens.ValueKind == JsonValueKind.Object;
    }

    private static bool HasApiKeyString( JsonDocument document )
    {
        return document.RootElement.TryGetProperty( ApiKeyProperty, out var apiKey ) && apiKey.ValueKind == JsonValueKind.String;
    }
}

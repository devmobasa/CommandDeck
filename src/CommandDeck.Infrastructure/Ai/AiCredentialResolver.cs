using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

public interface IAiCredentialResolver
{
    ValueTask<AiCredentialLease?> ResolveAsync( AiCredentialOptions reference, CancellationToken cancellationToken );
}

public sealed class AiCredentialLease : IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new( false, true );
    private byte[]? _bytes;

    public AiCredentialLease( byte[] bytes ) => _bytes = bytes;

    internal bool IsDisposed => _bytes is null;

    public string ReadUtf8( )
    {
        var bytes = _bytes;
        if ( bytes is null )
        {
            throw new ObjectDisposedException( nameof( AiCredentialLease ) );
        }

        return StrictUtf8.GetString( bytes );
    }

    public void Dispose( )
    {
        if ( _bytes is not null )
        {
            CryptographicOperations.ZeroMemory( _bytes );
            _bytes = null;
        }
    }
}

public sealed class AiCredentialResolver : IAiCredentialResolver
{
    private const int MaximumCredentialNameCharacters = 64;
    private const int MaximumTrailingNewlineBytes = 2;

    public async ValueTask<AiCredentialLease?> ResolveAsync( AiCredentialOptions reference, CancellationToken cancellationToken )
    {
        if ( reference is null )
        {
            throw new ArgumentNullException( nameof( reference ) );
        }

        if ( reference.Kind == AiCredentialKinds.None )
        {
            return null;
        }

        AiContractValidator.ValidateIdentifier( reference.Name, MaximumCredentialNameCharacters, "AI credential name" );

        byte[] bytes;

        switch ( reference.Kind )
        {
            case AiCredentialKinds.Environment:
                bytes = ReadEnvironment( reference.Name! );

                break;

            case AiCredentialKinds.Systemd:
                bytes = await ReadSystemdAsync( reference.Name!, cancellationToken )
                    .ConfigureAwait( false );

                break;

            default:
                throw CredentialFailure( "The configured AI credential kind is invalid." );
        }

        bytes = TrimOneNewline( bytes );

        try
        {
            _ = new UTF8Encoding( false, true ).GetCharCount( bytes );
        }
        catch ( DecoderFallbackException )
        {
            CryptographicOperations.ZeroMemory( bytes );

            throw CredentialFailure( "The configured AI credential is not valid UTF-8." );
        }

        if ( IsCredentialInvalid( bytes ) )
        {
            CryptographicOperations.ZeroMemory( bytes );

            throw CredentialFailure( "The configured AI credential is unavailable or invalid." );
        }

        return new AiCredentialLease( bytes );
    }

    private static byte[] ReadEnvironment( string name )
    {
        var value = Environment.GetEnvironmentVariable( name );
        if ( string.IsNullOrEmpty( value ) )
        {
            throw CredentialFailure( "The configured AI credential is unavailable." );
        }

        return Encoding.UTF8.GetBytes( value );
    }

    private static async ValueTask<byte[]> ReadSystemdAsync( string name, CancellationToken cancellationToken )
    {
        var directory = Environment.GetEnvironmentVariable( "CREDENTIALS_DIRECTORY" );
        if ( IsCredentialDirectoryInvalid( directory ) )
        {
            throw CredentialFailure( "The systemd credential directory is unavailable." );
        }

        using var credential = SafeRegularFile.OpenFromDirectory( directory, name );
        if ( credential.Length > AiLimits.MaximumCredentialBytes + MaximumTrailingNewlineBytes )
        {
            throw CredentialFailure( "The configured systemd credential is unavailable." );
        }

        var buffer = new byte[AiLimits.MaximumCredentialBytes + MaximumTrailingNewlineBytes + 1];

        try
        {
            var bytesRead = await credential
                .ReadPrefixAsync( buffer, cancellationToken )
                .ConfigureAwait( false );

            if ( HasCredentialIdentityChanged( credential, bytesRead ) )
            {
                throw CredentialFailure( "The configured systemd credential changed while reading." );
            }

            return buffer[..bytesRead];
        }
        finally
        {
            CryptographicOperations.ZeroMemory( buffer );
        }
    }

    private static byte[] TrimOneNewline( byte[] bytes )
    {
        var length = bytes.Length;
        if ( EndsWithNewline( bytes, length ) )
        {
            length--;

            if ( EndsWithCarriageReturn( bytes, length ) )
            {
                length--;
            }
        }

        if ( length == bytes.Length )
        {
            return bytes;
        }

        var trimmed = bytes[..length];
        CryptographicOperations.ZeroMemory( bytes );

        return trimmed;
    }

    private static AiProviderException CredentialFailure( string summary ) => new( AiErrorCategories.CredentialMissing, safeSummary: summary );

    private static bool IsCredentialInvalid( byte[] bytes )
    {
        return bytes.Length is < 1 or > AiLimits.MaximumCredentialBytes || bytes.Contains( ( byte ) 0 );
    }

    private static bool IsCredentialDirectoryInvalid( [System.Diagnostics.CodeAnalysis.NotNullWhen( false )] string? directory )
    {
        return string.IsNullOrWhiteSpace( directory ) || !Path.IsPathFullyQualified( directory );
    }

    private static bool HasCredentialIdentityChanged( SafeRegularFile credential, int bytesRead )
    {
        return bytesRead > AiLimits.MaximumCredentialBytes + MaximumTrailingNewlineBytes ||
               !LinuxMountIdentity.TryGet( credential.Handle, out var after ) ||
               after != credential.Identity;
    }

    private static bool EndsWithNewline( byte[] bytes, int length )
    {
        return length > 0 && bytes[length - 1] == ( byte ) '\n';
    }

    private static bool EndsWithCarriageReturn( byte[] bytes, int length )
    {
        return length > 0 && bytes[length - 1] == ( byte ) '\r';
    }
}

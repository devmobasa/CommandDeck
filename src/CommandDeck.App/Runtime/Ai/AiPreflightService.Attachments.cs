using System.Security.Cryptography;
using System.Text;

namespace CommandDeck;

public sealed partial class AiPreflightService
{
    private static bool Matches( AiAttachmentAuthority expected, AiAttachmentAuthority current ) =>
        string.Equals( expected.Kind, current.Kind, StringComparison.Ordinal ) &&
        string.Equals( expected.DisplayName, current.DisplayName, StringComparison.Ordinal ) &&
        expected.Utf8Bytes == current.Utf8Bytes &&
        string.Equals( expected.CanonicalPath, current.CanonicalPath, StringComparison.Ordinal ) &&
        expected.FileLength == current.FileLength &&
        expected.MountId == current.MountId &&
        expected.DeviceMajor == current.DeviceMajor &&
        expected.DeviceMinor == current.DeviceMinor &&
        expected.Inode == current.Inode &&
        expected.ModificationSeconds == current.ModificationSeconds &&
        expected.ModificationNanoseconds == current.ModificationNanoseconds &&
        expected.ChangeSeconds == current.ChangeSeconds &&
        expected.ChangeNanoseconds == current.ChangeNanoseconds &&
        CryptographicOperations.FixedTimeEquals( expected.ContentDigest, current.ContentDigest );

    private async ValueTask<(AiPreflightAttachment Attachment, AiAttachmentAuthority Authority)> ResolveAttachmentAsync( string clientOwnerId,
        AiAttachmentDraft draft, CancellationToken cancellationToken )
    {
        string text;
        string? canonicalPath = null;
        long? fileLength = null;
        ulong? mountId = null;
        uint? deviceMajor = null;
        uint? deviceMinor = null;
        ulong? inode = null;
        long? modificationSeconds = null;
        uint? modificationNanoseconds = null;
        long? changeSeconds = null;
        uint? changeNanoseconds = null;
        var display = draft.DisplayName ?? draft.Kind;
        if ( draft.Kind == AiAttachmentKinds.Text )
        {
            text = draft.Text!;
        }
        else
        {
            var result = _resolveResult( clientOwnerId, draft.ResultId! );
            if ( result is null )
            {
                throw new InvalidOperationException( "The selected result has expired." );
            }

            if ( ClipboardPrivacy.IsHistory( result ) )
            {
                throw new InvalidOperationException( "Clipboard history cannot be attached to AI requests." );
            }

            if ( PrivateSources.IsMail( result ) )
            {
                throw new InvalidOperationException( "HEY mail cannot be attached to AI requests." );
            }

            display = draft.DisplayName ?? result.Title;

            if ( draft.Kind == AiAttachmentKinds.File )
            {
                if ( result.Kind != ResultKinds.File || !result.Metadata.TryGetValue( ResultMetadataKeys.Path, out var path ) )
                {
                    throw new InvalidOperationException( "The selected result is not an attachable file." );
                }

                var file = await _files
                    .ReadAiAttachmentAsync( path, cancellationToken )
                    .ConfigureAwait( false );

                text = file.Text;
                canonicalPath = file.CanonicalPath;
                fileLength = file.FileLength;
                mountId = file.MountId;
                deviceMajor = file.DeviceMajor;
                deviceMinor = file.DeviceMinor;
                inode = file.Inode;
                modificationSeconds = file.ModificationSeconds;
                modificationNanoseconds = file.ModificationNanoseconds;
                changeSeconds = file.ChangeSeconds;
                changeNanoseconds = file.ChangeNanoseconds;
            }
            else
            {
                text = string.Join( '\n', GetResultAttachmentLines( result ) );
            }
        }

        if ( Encoding.UTF8.GetByteCount( text ) > AiLimits.MaximumAttachmentBytes )
        {
            throw new InvalidOperationException( "An AI attachment exceeds 64 KiB." );
        }

        AiPreflightAttachment attachment = new( )
        {
            Kind = draft.Kind,
            DisplayName = display,
            Text = text,
            Utf8Bytes = Encoding.UTF8.GetByteCount( text )
        };

        AiAttachmentDraft snapshot = new( )
        {
            Kind = draft.Kind,
            Text = draft.Text,
            ResultId = draft.ResultId,
            DisplayName = draft.DisplayName
        };

        return (attachment,
            new AiAttachmentAuthority( snapshot, attachment.Kind, attachment.DisplayName, attachment.Utf8Bytes,
                SHA256.HashData( Encoding.UTF8.GetBytes( text ) ), canonicalPath, fileLength, mountId, deviceMajor, deviceMinor,
                inode, modificationSeconds, modificationNanoseconds, changeSeconds, changeNanoseconds ));
    }

    private static IEnumerable<string?> GetResultAttachmentLines( SearchResult result )
    {
        return new[] { result.Title, result.Subtitle }
            .Where( static value => !string.IsNullOrWhiteSpace( value ) )
            .Concat( result.Details.Select( static field => $"{field.Label}: {field.Value}" ) );
    }
}

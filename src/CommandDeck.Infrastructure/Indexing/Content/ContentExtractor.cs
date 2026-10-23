using System.Text;

namespace CommandDeck;

public sealed class ContentExtractor : IDisposable
{
    private const int MaximumConcurrentExtractors = 16;

    private const string InputSizeLimitReason = "input-size-limit";
    private const string ArchiveLimitReason = "archive-invalid-or-limit";
    private const string ExtractorTimeoutReason = "extractor-timeout";
    private const string OutputLimitReason = "extractor-output-limit";
    private const string MemoryLimitReason = "extractor-memory-limit";
    private const string ExtractorExitReason = "extractor-nonzero-exit";

    public const string ExtractorVersion = "4";
    private const string UnreadableFileReason = "unreadable-or-special-file";
    private const int BinaryProbeBytes = 8_192;

    private static readonly HashSet<string> PlainTextExtensions = new( StringComparer.OrdinalIgnoreCase )
    {
        ".txt",
        ".md",
        ".markdown",
        ".rst",
        ".org",
        ".adoc",
        ".cs",
        ".fs",
        ".vb",
        ".cpp",
        ".cc",
        ".c",
        ".h",
        ".hpp",
        ".rs",
        ".go",
        ".java",
        ".kt",
        ".kts",
        ".swift",
        ".js",
        ".jsx",
        ".ts",
        ".tsx",
        ".mjs",
        ".cjs",
        ".py",
        ".rb",
        ".php",
        ".sh",
        ".bash",
        ".zsh",
        ".fish",
        ".lua",
        ".qml",
        ".sql",
        ".graphql",
        ".gql",
        ".json",
        ".jsonc",
        ".toml",
        ".yaml",
        ".yml",
        ".xml",
        ".ini",
        ".conf",
        ".cfg",
        ".desktop",
        ".service",
        ".socket",
        ".timer",
        ".target",
        ".mount",
        ".path",
        ".env",
        ".log",
        ".css",
        ".scss",
        ".less",
        ".html",
        ".htm",
        ".vue",
        ".svelte"
    };

    private static readonly HashSet<string> PandocExtensions = new( StringComparer.OrdinalIgnoreCase )
    {
        ".docx",
        ".odt",
        ".epub",
        ".rtf"
    };

    private readonly IndexingOptions _options;
    private readonly SemaphoreSlim _externalExtractorSlots;

    public ContentExtractor( IndexingOptions options )
    {
        _options = options;

        _externalExtractorSlots = new SemaphoreSlim( Math.Clamp( options.MaximumConcurrentExtractors, 1, MaximumConcurrentExtractors ) );
    }

    public async ValueTask<ContentExtractionOutcome> ExtractAsync( string path, long size, FileIndexPolicy policy, CancellationToken cancellationToken )
    {
        var extension = Path.GetExtension( path );
        var limits = ResolveLimits( path, policy );

        if ( IsTextOrConfiguration( path, extension ) )
        {
            if ( size > limits.MaximumFileBytes )
            {
                return ContentExtractionOutcome.Failed( InputSizeLimitReason );
            }

            using var text = OpenRegularFile( path );
            if ( text is null )
            {
                return ContentExtractionOutcome.Failed( UnreadableFileReason );
            }

            return ContentExtractionOutcome.Succeeded( await ReadTextSafelyAsync( text, limits.MaximumExtractedBytes, cancellationToken )
                .ConfigureAwait( false ) );
        }

        if ( CanExtractPdf( size, extension, limits ) )
        {
            return await RunExtractorAsync( ToolExecutables.PdfToText, ["-layout", "-nopgbrk", path, "-"], path, limits, cancellationToken )
                .ConfigureAwait( false );
        }

        if ( CanExtractWithPandoc( size, extension, limits ) )
        {
            return await RunExtractorAsync( ToolExecutables.Pandoc, ["--to=plain", "--wrap=none", path], path, limits, cancellationToken )
                .ConfigureAwait( false );
        }

        if ( CanExtractArchive( path, size, limits ) )
        {
            using var input = OpenRegularFile( path );
            if ( input is null )
            {
                return ContentExtractionOutcome.Failed( UnreadableFileReason );
            }

            var archive = await ArchiveContentExtractor
                .ExtractAsync( input, path, size, limits.MaximumExtractedBytes, cancellationToken )
                .ConfigureAwait( false );

            if ( archive is null )
            {
                return ContentExtractionOutcome.Failed( ArchiveLimitReason );
            }

            return ContentExtractionOutcome.Succeeded( archive );
        }

        return ContentExtractionOutcome.NotAttempted;
    }

    public long? GetMaximumBytes( string path, FileIndexPolicy policy )
    {
        var extension = Path.GetExtension( path );
        if ( IsTextOrConfiguration( path, extension ) )
        {
            return ResolveLimits( path, policy ).MaximumFileBytes;
        }

        if ( IsExternalExtractorEnabled( extension ) )
        {
            return ResolveLimits( path, policy ).MaximumFileBytes;
        }

        if ( ArchiveContentExtractor.IsSupported( path ) )
        {
            return ResolveLimits( path, policy ).MaximumFileBytes;
        }

        return null;
    }

    // Extraction admits only regular files: opening a FIFO or device by path
    // can block indefinitely, so every reader starts from a verified handle.
    private static SafeRegularFile? OpenRegularFile( string path )
    {
        try
        {
            return SafeRegularFile.Open( path, followLeafSymbolicLink: false );
        }
        catch ( Exception exception ) when ( exception is InvalidOperationException or IOException or UnauthorizedAccessException )
        {
            return null;
        }
    }

    private static async ValueTask<string?> ReadTextSafelyAsync( SafeRegularFile file, int maximumExtractedBytes, CancellationToken cancellationToken )
    {
        var bytes = new byte[Math.Min( file.Length, Math.Max( maximumExtractedBytes, BinaryProbeBytes ) )];

        var bytesRead = await file
            .ReadPrefixAsync( bytes, cancellationToken )
            .ConfigureAwait( false );

        var probeLength = Math.Min( bytesRead, BinaryProbeBytes );
        for ( var index = 0; index < probeLength; index++ )
        {
            if ( bytes[index] == 0 )
            {
                return null;
            }
        }

        ReadOnlySpan<byte> content = bytes.AsSpan( 0, Math.Min( bytesRead, maximumExtractedBytes ) );

        try
        {
            return new UTF8Encoding( encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true ).GetString( content );
        }
        catch ( DecoderFallbackException )
        {
            return Encoding.UTF8.GetString( content );
        }
    }

    private async ValueTask<ContentExtractionOutcome> RunExtractorAsync( string executable, IReadOnlyList<string> arguments, string input,
        ExtractionLimits limits, CancellationToken cancellationToken )
    {
        // External tools need the named path for format detection; admit its
        // type first so a special file never reaches a child process.
        using var admitted = OpenRegularFile( input );
        if ( admitted is null )
        {
            return ContentExtractionOutcome.Failed( UnreadableFileReason );
        }

        if ( !ProcessRunner.ExistsOnPath( executable ) )
        {
            return ContentExtractionOutcome.Failed( $"extractor-missing:{executable}" );
        }

        await _externalExtractorSlots
            .WaitAsync( cancellationToken )
            .ConfigureAwait( false );

        ProcessResult result;

        try
        {
            result = await ProcessRunner
                .RunBoundedAsync( executable, arguments, workingDirectory: null, TimeSpan.FromMilliseconds( limits.TimeoutMilliseconds ),
                    CreateExtractorLimits( limits ), cancellationToken )
                .ConfigureAwait( false );
        }
        finally
        {
            _externalExtractorSlots.Release( );
        }

        if ( result.TimedOut )
        {
            return ContentExtractionOutcome.Failed( ExtractorTimeoutReason );
        }

        if ( result.AnyOutputLimitExceeded )
        {
            return ContentExtractionOutcome.Failed( OutputLimitReason );
        }

        if ( result.MemoryLimitExceeded )
        {
            return ContentExtractionOutcome.Failed( MemoryLimitReason );
        }

        if ( result.ExitCode == 0 )
        {
            return ContentExtractionOutcome.Succeeded( result.StandardOutput );
        }

        return ContentExtractionOutcome.Failed( ExtractorExitReason );
    }

    private ExtractionLimits ResolveLimits( string path, FileIndexPolicy policy )
    {
        var extension = Path.GetExtension( path );
        var defaultFileBytes = ResolveDefaultFileBytes( path, extension );

        return new ExtractionLimits( policy.MaximumFileBytes ?? defaultFileBytes, policy.MaximumExtractedBytes ?? _options.MaximumExtractedBytes,
            policy.ExtractorTimeoutMilliseconds ?? _options.ExtractorTimeoutMilliseconds,
            policy.ExtractorMemoryLimitBytes ?? _options.ExtractorMemoryLimitBytes );
    }

    private readonly record struct ExtractionLimits( long MaximumFileBytes, int MaximumExtractedBytes, int TimeoutMilliseconds, long MaximumMemoryBytes );

    public void Dispose( ) => _externalExtractorSlots.Dispose( );

    private static bool IsTextOrConfiguration( string path, string extension )
    {
        return PlainTextExtensions.Contains( extension ) || StructuredConfigExtractor.IsStructured( path );
    }

    private bool CanExtractPdf( long size, string extension, ExtractionLimits limits )
    {
        return extension.Equals( FileExtensions.Pdf, StringComparison.OrdinalIgnoreCase ) && _options.EnablePdfExtraction && size <= limits.MaximumFileBytes;
    }

    private bool CanExtractWithPandoc( long size, string extension, ExtractionLimits limits )
    {
        return PandocExtensions.Contains( extension ) && _options.EnablePandocExtraction && size <= limits.MaximumFileBytes;
    }

    private static bool CanExtractArchive( string path, long size, ExtractionLimits limits )
    {
        return ArchiveContentExtractor.IsSupported( path ) && size <= limits.MaximumFileBytes;
    }

    private bool IsExternalExtractorEnabled( string extension )
    {
        return extension.Equals( FileExtensions.Pdf, StringComparison.OrdinalIgnoreCase ) && _options.EnablePdfExtraction ||
               PandocExtensions.Contains( extension ) && _options.EnablePandocExtraction;
    }

    private long ResolveDefaultFileBytes( string path, string extension )
    {
        if ( IsTextOrConfiguration( path, extension ) )
        {
            return _options.MaximumTextFileBytes;
        }

        return _options.MaximumDocumentFileBytes;
    }

    private ProcessExecutionLimits CreateExtractorLimits( ExtractionLimits limits )
    {
        return new ProcessExecutionLimits
        {
            MaximumStandardOutputBytes = limits.MaximumExtractedBytes,
            MaximumStandardErrorBytes = _options.ExtractorStandardErrorBytes,
            MaximumMemoryBytes = limits.MaximumMemoryBytes,
            ClearEnvironment = true
        };
    }
}

public sealed record ContentExtractionOutcome( string? Text, string? FailureReason )
{
    public static ContentExtractionOutcome NotAttempted { get; } = new( null, null );

    public static ContentExtractionOutcome Succeeded( string? text ) => new( text, null );

    public static ContentExtractionOutcome Failed( string reason ) => new( null, reason );
}

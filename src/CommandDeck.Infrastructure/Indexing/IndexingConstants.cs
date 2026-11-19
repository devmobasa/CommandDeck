namespace CommandDeck;

internal static class IndexingConstants
{
    internal const string RedactedDiagnosticPath = "[redacted]";
    internal const string IndexRootIdKind = "index-root";
    internal const string RootCompleteStage = "root-complete";
    internal const string ExtractorChangedReason = "extractor-version-changed";
    internal const string GitDirectoryName = ".git";
    internal const string PythonEnvironmentMarker = "pyvenv.cfg";

    // A folder holding this file, starting with the signature, is a cache.
    // See https://bford.info/cachedir/.
    internal const string CacheDirectoryTag = "CACHEDIR.TAG";
    internal const string CacheDirectoryTagSignature = "Signature: 8a477f597d28d172789f06886806bc55";
}

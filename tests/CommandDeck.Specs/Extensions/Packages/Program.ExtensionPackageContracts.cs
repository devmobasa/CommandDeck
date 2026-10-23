using CommandDeck.ExtensionProtocol;

namespace CommandDeck.Specs;

public static partial class Program
{
    private static void ExtensionPackageAndProbeContracts( )
    {
        Equal( "application-x-addon", ExtensionIcons.Addon );
        Equal( "assets", ExtensionPackageFiles.AssetsDirectory );
        Equal( "assets/", ExtensionPackageFiles.AssetPrefix );
        Equal( ".development", ExtensionPackageFiles.DevelopmentIdSuffix );
        Equal( "searchItemId", ExtensionSearchInputNames.SearchItemId );
        True( ExtensionTargets.ElfMagic.SequenceEqual( "\u007fELF"u8 ) );
        Equal( 64, ExtensionStorageLimits.MaximumEntries );
        Equal( 16384, ExtensionStorageLimits.MaximumValueBytes );
        Equal( 65536, ExtensionStorageLimits.MaximumInstallationBytes );
        Equal( 524288000L, ExtensionPackageLimits.MaximumExpandedBytes );
        Equal( 10000, ExtensionPackageLimits.MaximumArchiveEntries );
        Equal( 32, ExtensionCommandLimits.MaximumCommands );
        Equal( "clipboard-host", DaemonChildProcessContract.ClipboardHost );
        Equal( "extension-worker", DaemonChildProcessContract.ExtensionWorker );
        Equal( "--capture", DaemonChildProcessContract.Capture );
        Equal( "--control-read", DaemonChildProcessContract.ControlRead );
        Equal( "--control-write", DaemonChildProcessContract.ControlWrite );
        Equal( "--host-epoch", DaemonChildProcessContract.HostEpoch );
        Equal( "--debug-socket", DaemonChildProcessContract.DebugSocket );
        Equal( "--artifact", DaemonChildProcessContract.Artifact );
        Equal( "--entrypoint", DaemonChildProcessContract.Entrypoint );
        Equal( "--owner-pid", DaemonChildProcessContract.OwnerPid );
        Equal( "--owner-start", DaemonChildProcessContract.OwnerStart );
        Equal( "PROBE_READY ", ExtensionProbeContract.ReadyPrefix );
        Equal( "storage.get", ExtensionProbeContract.StorageGetMethod );
        Equal( "hostFile", ExtensionProbeFields.HostFile );
        Equal( "hostSocket", ExtensionProbeFields.HostSocket );
        Equal( "hostHome", ExtensionProbeFields.HostHome );
        Equal( "port", ExtensionProbeFields.Port );
        Equal( "fileDenied,homeDenied,socketDenied,networkDenied,packageWriteDenied,temporaryWorks,environmentClean,ptraceDenied",
            string.Join( ',', ExtensionProbeFields.RequiredReportFields ) );

        Equal( "org.commanddeck.Package1", PackageHelperProtocol.BusName );
        Equal( "org.commanddeck.Package1", PackageHelperProtocol.InterfaceName );
        Equal( "GetStatus", PackageHelperProtocol.MethodGetStatus );
        Equal( "Commit", PackageHelperProtocol.MethodCommit );
        Equal( "GetOperation", PackageHelperProtocol.MethodGetOperation );
        Equal( "Cancel", PackageHelperProtocol.MethodCancel );
        Equal( "Progress", PackageHelperProtocol.SignalProgress );
    }
}

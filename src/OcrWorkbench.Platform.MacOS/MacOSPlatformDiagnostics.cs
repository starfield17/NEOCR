namespace OcrWorkbench.Platform.MacOS;

public static class MacOSPlatformDiagnostics
{
    public static int NativeAbiVersion => OperatingSystem.IsMacOS()
        ? MacOSNativeMethods.GetAbiVersion()
        : throw new PlatformNotSupportedException("The macOS native ABI is available on macOS only.");
}

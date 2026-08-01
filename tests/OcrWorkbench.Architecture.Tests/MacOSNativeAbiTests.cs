using System.Runtime.InteropServices;
using OcrWorkbench.Platform.MacOS;

namespace OcrWorkbench.Architecture.Tests;

public sealed class MacOSNativeAbiTests
{
    [Fact]
    public void Native_shim_loads_and_reports_version_one()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        Assert.Equal(2, MacOSPlatformDiagnostics.NativeAbiVersion);

        Assert.True(NativeLibrary.TryLoad(
            "neocr_macos",
            typeof(MacOSPlatformDiagnostics).Assembly,
            DllImportSearchPath.ApplicationDirectory,
            out var handle));
        try
        {
            Assert.True(NativeLibrary.TryGetExport(handle, "neocr_capture_preflight_access", out _));
            Assert.True(NativeLibrary.TryGetExport(handle, "neocr_capture_request_access", out _));
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}

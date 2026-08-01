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

        Assert.Equal(1, MacOSPlatformDiagnostics.NativeAbiVersion);
    }
}

using System.Runtime.InteropServices;
using System.Text.Json;
using OcrWorkbench.Contracts;
using OcrWorkbench.PluginHost;

namespace OcrWorkbench.Core.Tests;

public sealed class PluginPackageTests
{
    [Fact]
    public async Task LoadsCompatiblePackage()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var entrypoint = Path.Combine(directory, "worker.dll");
            await File.WriteAllBytesAsync(entrypoint, [0]);
            await WriteManifestAsync(directory, "worker.dll", [RuntimeInformation.RuntimeIdentifier]);

            var package = await PluginPackage.LoadAsync(directory);

            Assert.Equal("org.ocrworkbench.test", package.Manifest.Id);
            Assert.Equal(entrypoint, package.EntrypointPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectsEntrypointOutsidePackage()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await WriteManifestAsync(directory, "../worker.dll", ["any"]);

            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => PluginPackage.LoadAsync(directory));

            Assert.Contains("escapes", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteManifestAsync(
        string directory,
        string entrypoint,
        IReadOnlyList<string> rids)
    {
        var manifest = new PluginManifest(
            1,
            "org.ocrworkbench.test",
            "1.0.0",
            "recognizer",
            entrypoint,
            1,
            1,
            rids,
            ["recognize.image"],
            "Apache-2.0");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "plugin.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ocr-workbench-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}


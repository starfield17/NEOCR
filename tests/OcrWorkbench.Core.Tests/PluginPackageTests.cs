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
            await DeleteTemporaryDirectoryAsync(directory);
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
            await DeleteTemporaryDirectoryAsync(directory);
        }
    }

    [Fact]
    public async Task Rejects_worker_identity_that_does_not_match_manifest()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = GetFakeWorkerOutputDirectory();
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
            }

            await WriteManifestAsync(directory, "OcrWorkbench.FakeWorker.dll", ["any"]);
            var package = await PluginPackage.LoadAsync(directory);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() => package.StartRecognizerAsync());

            Assert.Contains("does not match manifest", error.Message);
        }
        finally
        {
            await DeleteTemporaryDirectoryAsync(directory);
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

    private static async Task DeleteTemporaryDirectoryAsync(string directory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 20 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private static string GetFakeWorkerOutputDirectory()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OcrWorkbench.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    "workers",
                    "OcrWorkbench.FakeWorker",
                    "bin",
                    configuration,
                    "net10.0");
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

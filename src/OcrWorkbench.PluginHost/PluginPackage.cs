using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OcrWorkbench.Contracts;

namespace OcrWorkbench.PluginHost;

public sealed partial class PluginPackage
{
    private PluginPackage(string directory, string entrypointPath, PluginManifest manifest)
    {
        Directory = directory;
        EntrypointPath = entrypointPath;
        Manifest = manifest;
    }

    public string Directory { get; }
    public string EntrypointPath { get; }
    public PluginManifest Manifest { get; }

    public static async Task<PluginPackage> LoadAsync(
        string packageDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(packageDirectory);
        var manifestPath = Path.Combine(directory, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"Plugin manifest was not found: {manifestPath}");
        }

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Plugin manifest is empty.");
        ValidateManifest(manifest);

        var entrypoint = Path.GetFullPath(Path.Combine(directory, manifest.Entrypoint));
        if (!IsDescendant(directory, entrypoint))
        {
            throw new InvalidDataException("Plugin entrypoint escapes the package directory.");
        }

        if (!File.Exists(entrypoint))
        {
            throw new InvalidDataException($"Plugin entrypoint was not found: {entrypoint}");
        }

        var currentRid = RuntimeInformation.RuntimeIdentifier;
        if (!manifest.RuntimeIdentifiers.Contains("any", StringComparer.OrdinalIgnoreCase)
            && !manifest.RuntimeIdentifiers.Contains(currentRid, StringComparer.OrdinalIgnoreCase))
        {
            throw new PlatformNotSupportedException(
                $"Plugin '{manifest.Id}' does not support RID '{currentRid}'.");
        }

        return new PluginPackage(directory, entrypoint, manifest);
    }

    public Task<WorkerProcessRecognizer> StartRecognizerAsync(
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        EnsureRecognizer();

        return StartAndValidateRecognizerAsync(log, cancellationToken);
    }

    public WorkerRecognizerSession CreateRecognizerSession(Action<string>? log = null)
    {
        EnsureRecognizer();
        return new WorkerRecognizerSession(this, log);
    }

    private void EnsureRecognizer()
    {
        if (!string.Equals(Manifest.Kind, "recognizer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Plugin '{Manifest.Id}' is not a recognizer.");
        }
    }

    private async Task<WorkerProcessRecognizer> StartAndValidateRecognizerAsync(
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var recognizer = await WorkerProcessRecognizer
            .StartAsync(EntrypointPath, log, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(recognizer.PluginId, Manifest.Id, StringComparison.Ordinal)
            || !string.Equals(recognizer.PluginVersion, Manifest.Version, StringComparison.Ordinal))
        {
            await recognizer.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException(
                $"Worker identity '{recognizer.PluginId}@{recognizer.PluginVersion}' does not match manifest '{Manifest.Id}@{Manifest.Version}'.");
        }

        return recognizer;
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (manifest.ManifestVersion != 1)
        {
            throw new InvalidDataException($"Unsupported manifest version: {manifest.ManifestVersion}.");
        }

        if (!PluginIdPattern().IsMatch(manifest.Id))
        {
            throw new InvalidDataException($"Invalid plugin ID: '{manifest.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.Kind)
            || string.IsNullOrWhiteSpace(manifest.Entrypoint)
            || string.IsNullOrWhiteSpace(manifest.License))
        {
            throw new InvalidDataException("Plugin version, kind, entrypoint and license are required.");
        }

        if (manifest.ProtocolMinimum > PluginFraming.CurrentProtocolVersion
            || manifest.ProtocolMaximum < PluginFraming.CurrentProtocolVersion
            || manifest.ProtocolMinimum > manifest.ProtocolMaximum)
        {
            throw new InvalidDataException(
                $"Plugin protocol range {manifest.ProtocolMinimum}-{manifest.ProtocolMaximum} is incompatible with host protocol {PluginFraming.CurrentProtocolVersion}.");
        }

        if (manifest.RuntimeIdentifiers.Count == 0)
        {
            throw new InvalidDataException("Plugin must declare at least one runtime identifier.");
        }
    }

    private static bool IsDescendant(string directory, string candidate)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PluginIdPattern();
}

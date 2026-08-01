using System.Text.Json.Serialization;

namespace OcrWorkbench.Contracts;

public sealed record PluginManifest(
    [property: JsonPropertyName("manifestVersion")] int ManifestVersion,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("entrypoint")] string Entrypoint,
    [property: JsonPropertyName("protocolMinimum")] uint ProtocolMinimum,
    [property: JsonPropertyName("protocolMaximum")] uint ProtocolMaximum,
    [property: JsonPropertyName("runtimeIdentifiers")] IReadOnlyList<string> RuntimeIdentifiers,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("license")] string License);


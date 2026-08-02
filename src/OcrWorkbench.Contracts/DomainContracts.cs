namespace OcrWorkbench.Contracts;

public sealed record JobSpec(
    IReadOnlyList<ImageInput> Inputs,
    RecognitionOptions Recognition,
    PlainTextExportOptions Export);

public sealed record ImageInput(string Path, string MimeType = "application/octet-stream");

public sealed record RecognitionOptions(string Language = "auto");

public sealed record PlainTextExportOptions(string OutputPath);

public sealed record RecognizerIdentity(string Id, string Version);

public enum JobState
{
    Queued = 1,
    Running = 2,
    Pausing = 3,
    Paused = 4,
    Completed = 5,
    CompletedWithErrors = 6,
    Failed = 7,
    Cancelled = 8,
}

public sealed record PageArtifact(
    string StableId,
    string SourcePath,
    string MimeType,
    int PageNumber = 1);

public sealed record SpatialPoint(double X, double Y);

public sealed record SpatialTextBlock(
    string Text,
    IReadOnlyList<SpatialPoint> Polygon,
    double Confidence,
    string Source);

public sealed record RecognitionResult(
    IReadOnlyList<SpatialTextBlock> SpatialBlocks,
    string? SemanticMarkdown = null);

public abstract record PluginOutcome<T>
{
    private PluginOutcome() { }

    public sealed record Succeeded(T Value) : PluginOutcome<T>;

    public sealed record Declined(
        string ReasonCode,
        string Message,
        bool Retryable,
        IReadOnlyList<string> AllowedFallbackClasses) : PluginOutcome<T>;

    public sealed record Failed(string ErrorCode, string Message, bool Retryable) : PluginOutcome<T>;
}

public static class DeclineReasonCodes
{
    public const string CapabilityMissing = "Capability.Missing";
    public const string PrivacyLocalDataRequired = "Privacy.LocalDataRequired";
    public const string OutputRequiresGeometry = "Output.RequiresGeometry";
    public const string PlatformUnsupported = "Platform.Unsupported";
}

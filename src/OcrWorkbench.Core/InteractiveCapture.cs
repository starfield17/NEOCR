namespace OcrWorkbench.Core;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Option = 2,
    Shift = 4,
    Command = 8,
}

public sealed record HotkeyGesture(string Key, HotkeyModifiers Modifiers)
{
    public static HotkeyGesture DefaultScreenshot { get; } = new("O", HotkeyModifiers.Control | HotkeyModifiers.Option);
}

public interface IGlobalHotkeyService : IAsyncDisposable
{
    event EventHandler? Pressed;

    ValueTask RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken = default);

    ValueTask UnregisterAsync(CancellationToken cancellationToken = default);
}

public interface IInteractiveScreenshotService
{
    Task<ScreenshotCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default);
}

public abstract record ScreenshotCaptureResult
{
    private ScreenshotCaptureResult() { }

    public sealed record Succeeded(byte[] PngBytes, int PixelWidth, int PixelHeight) : ScreenshotCaptureResult;

    public sealed record Cancelled : ScreenshotCaptureResult;

    public sealed record Failed(ScreenshotCaptureFailure Failure, string Message) : ScreenshotCaptureResult;
}

public enum ScreenshotCaptureFailure
{
    PermissionDenied = 1,
    Unsupported = 2,
    NativeFailure = 3,
}

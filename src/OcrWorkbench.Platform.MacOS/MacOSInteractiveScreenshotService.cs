using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OcrWorkbench.Core;

namespace OcrWorkbench.Platform.MacOS;

public sealed class MacOSInteractiveScreenshotService : IInteractiveScreenshotService, IScreenCapturePermissionService
{
    private sealed class CaptureOperation
    {
        public TaskCompletionSource<ScreenshotCaptureResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ValueTask<ScreenCapturePermissionStatus> RequestAccessAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOSVersionAtLeast(15, 2))
        {
            return ValueTask.FromResult(ScreenCapturePermissionStatus.Unsupported);
        }

        EnsureAbi();
        if (MacOSNativeMethods.PreflightCaptureAccess() == 1)
        {
            return ValueTask.FromResult(ScreenCapturePermissionStatus.Granted);
        }

        var status = MacOSNativeMethods.RequestCaptureAccess() == 1
            ? ScreenCapturePermissionStatus.Granted
            : ScreenCapturePermissionStatus.Denied;
        return ValueTask.FromResult(status);
    }

    public async Task<ScreenshotCaptureResult> CaptureRegionAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(15, 2))
        {
            return new ScreenshotCaptureResult.Failed(
                ScreenshotCaptureFailure.Unsupported,
                "Interactive screenshot OCR requires macOS 15.2 or newer.");
        }

        EnsureAbi();

        var operation = new CaptureOperation();
        var handle = GCHandle.Alloc(operation);
        var context = GCHandle.ToIntPtr(handle);
        var status = BeginNativeCapture(context);
        if (status != 0)
        {
            handle.Free();
            return status == 3
                ? new ScreenshotCaptureResult.Failed(ScreenshotCaptureFailure.NativeFailure, "A screenshot selection is already active.")
                : new ScreenshotCaptureResult.Failed(ScreenshotCaptureFailure.NativeFailure, $"Screenshot selection failed to start (native status {status}).");
        }

        using var cancellation = cancellationToken.Register(static () => MacOSNativeMethods.CancelCapture());
        return await operation.Completion.Task.ConfigureAwait(false);
    }

    private static unsafe int BeginNativeCapture(nint context) =>
        MacOSNativeMethods.BeginCapture(&CaptureCompleted, context);

    private static void EnsureAbi()
    {
        var version = MacOSNativeMethods.GetAbiVersion();
        if (version != 2)
        {
            throw new InvalidOperationException($"Unsupported macOS native ABI version: {version}.");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CaptureCompleted(
        int status,
        nint bytes,
        nuint length,
        int pixelWidth,
        int pixelHeight,
        nint errorMessage,
        nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        try
        {
            if (handle.Target is not CaptureOperation operation)
            {
                return;
            }

            ScreenshotCaptureResult result;
            if (status == 0 && bytes != nint.Zero && length > 0)
            {
                var managed = new byte[checked((int)length)];
                Marshal.Copy(bytes, managed, 0, managed.Length);
                result = new ScreenshotCaptureResult.Succeeded(managed, pixelWidth, pixelHeight);
            }
            else if (status == 1)
            {
                result = new ScreenshotCaptureResult.Cancelled();
            }
            else
            {
                var message = errorMessage == nint.Zero
                    ? "The native screenshot operation failed."
                    : Marshal.PtrToStringUTF8(errorMessage) ?? "The native screenshot operation failed.";
                var failure = status == 2
                    ? ScreenshotCaptureFailure.PermissionDenied
                    : ScreenshotCaptureFailure.NativeFailure;
                result = new ScreenshotCaptureResult.Failed(failure, message);
            }

            operation.Completion.TrySetResult(result);
        }
        catch (Exception exception)
        {
            if (handle.Target is CaptureOperation operation)
            {
                operation.Completion.TrySetException(exception);
            }
        }
        finally
        {
            if (bytes != nint.Zero)
            {
                MacOSNativeMethods.FreeBuffer(bytes);
            }

            handle.Free();
        }
    }
}

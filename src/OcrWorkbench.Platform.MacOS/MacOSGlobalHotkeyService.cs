using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OcrWorkbench.Core;

namespace OcrWorkbench.Platform.MacOS;

public sealed class MacOSGlobalHotkeyService : IGlobalHotkeyService
{
    private const uint CarbonControlKey = 1u << 12;
    private const uint CarbonOptionKey = 1u << 11;
    private const uint CarbonShiftKey = 1u << 9;
    private const uint CarbonCommandKey = 1u << 8;
    private const uint VirtualKeyO = 0x1F;

    private GCHandle _selfHandle;
    private bool _registered;

    public event EventHandler? Pressed;

    public unsafe ValueTask RegisterAsync(HotkeyGesture gesture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_registered)
        {
            throw new InvalidOperationException("A global hotkey is already registered.");
        }

        if (!string.Equals(gesture.Key, "O", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The macOS adapter currently supports the O key only.");
        }

        EnsureAbi();
        _selfHandle = GCHandle.Alloc(this);
        var status = MacOSNativeMethods.RegisterHotkey(
            VirtualKeyO,
            MapModifiers(gesture.Modifiers),
            &HotkeyPressed,
            GCHandle.ToIntPtr(_selfHandle));
        if (status != 0)
        {
            _selfHandle.Free();
            throw new InvalidOperationException($"The global shortcut could not be registered (native status {status}). It may already be in use.");
        }

        _registered = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask UnregisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_registered)
        {
            MacOSNativeMethods.UnregisterHotkey();
            _registered = false;
            _selfHandle.Free();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => UnregisterAsync();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void HotkeyPressed(nint context)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(context);
            if (handle.Target is MacOSGlobalHotkeyService service)
            {
                service.Pressed?.Invoke(service, EventArgs.Empty);
            }
        }
        catch
        {
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    private static uint MapModifiers(HotkeyModifiers modifiers)
    {
        var native = 0u;
        if (modifiers.HasFlag(HotkeyModifiers.Control)) native |= CarbonControlKey;
        if (modifiers.HasFlag(HotkeyModifiers.Option)) native |= CarbonOptionKey;
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) native |= CarbonShiftKey;
        if (modifiers.HasFlag(HotkeyModifiers.Command)) native |= CarbonCommandKey;
        return native;
    }

    private static void EnsureAbi()
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(15, 2))
        {
            throw new PlatformNotSupportedException("Interactive screenshot OCR requires macOS 15.2 or newer.");
        }

        var version = MacOSNativeMethods.GetAbiVersion();
        if (version != 1)
        {
            throw new InvalidOperationException($"Unsupported macOS native ABI version: {version}.");
        }
    }
}

using System.Runtime.InteropServices;

namespace OcrWorkbench.Platform.MacOS;

internal static unsafe partial class MacOSNativeMethods
{
    internal const string LibraryName = "neocr_macos";

    [LibraryImport(LibraryName, EntryPoint = "neocr_macos_abi_version")]
    internal static partial int GetAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "neocr_hotkey_register")]
    internal static partial int RegisterHotkey(
        uint virtualKeyCode,
        uint modifiers,
        delegate* unmanaged[Cdecl]<nint, void> callback,
        nint context);

    [LibraryImport(LibraryName, EntryPoint = "neocr_hotkey_unregister")]
    internal static partial void UnregisterHotkey();

    [LibraryImport(LibraryName, EntryPoint = "neocr_capture_begin")]
    internal static partial int BeginCapture(
        delegate* unmanaged[Cdecl]<int, nint, nuint, int, int, nint, nint, void> callback,
        nint context);

    [LibraryImport(LibraryName, EntryPoint = "neocr_capture_cancel")]
    internal static partial void CancelCapture();

    [LibraryImport(LibraryName, EntryPoint = "neocr_buffer_free")]
    internal static partial void FreeBuffer(nint buffer);
}

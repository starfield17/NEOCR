# macOS development

## Supported baseline

- macOS 15.2 or newer for interactive region capture.
- .NET 10 SDK.
- Xcode Command Line Tools providing `xcrun`, Apple Clang, AppKit, Carbon, ScreenCaptureKit, ImageIO, and UniformTypeIdentifiers.

The managed solution builds without invoking macOS code on other platforms. On macOS, the platform project compiles a universal `libneocr_macos.dylib` before its managed build.

## Commands

```sh
dotnet restore OcrWorkbench.slnx
dotnet build OcrWorkbench.slnx
dotnet test OcrWorkbench.slnx --no-build
dotnet run --project src/OcrWorkbench.Gui
build/macos/package.sh osx-arm64
```

The packaging script publishes a framework-dependent application, assembles `artifacts/NEOCR.app`, and applies an ad-hoc local signature. Screen Recording permission is associated with the running application identity. Use the packaged `.app` smoke path for final permission testing; a terminal-hosted `dotnet run` is suitable for build and non-permission UI checks only. Release distribution will require a stable Developer ID signature and notarization.

## Screen Recording authorization

Authorization is requested only after the user clicks **Screenshot OCR** or presses `Control+Option+O`. The sequence is permission request, recognizer-package validation, then region selection. A missing or invalid recognizer can no longer prevent the first authorization request.

For the deterministic development worker, obtain the absolute recognizer-package path from the repository and paste the command output into the GUI:

```sh
realpath workers/OcrWorkbench.FakeWorker/bin/Debug/net10.0
```

If access was denied previously, macOS may not show the dialog again. Enable NEOCR under **System Settings > Privacy & Security > Screen Recording**, then relaunch it. The packaging script uses an ad-hoc signature: do not rebuild between granting access and completing the manual test, and expect to re-grant access after a rebuild. Stable release permission identity requires Developer ID signing.

## Manual screenshot QA

1. Start with no recognizer configured and trigger `Control+Option+O` while another application is active; the authorization request must occur before the missing-plugin message.
2. Verify denial produces instructions rather than an overlay or crash.
3. Grant access, relaunch without rebuilding, and configure the fake worker directory.
4. Verify drag selection, `Esc` cancellation, primary/secondary displays, mixed scale, and a cross-display rectangle.
5. Verify PNG recognition, result copy, and temporary-file cleanup.
6. Trigger repeatedly during an active selection; only one overlay may exist.
7. Quit and verify the shortcut is no longer registered.
8. Run two OCR operations with the fake worker and verify the same worker remains active; cancel an active batch and verify its job is `Cancelled` and a later operation succeeds.

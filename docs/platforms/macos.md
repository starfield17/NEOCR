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

## Manual screenshot QA

1. Configure a valid recognizer package.
2. Trigger `Control+Option+O` while another application is active.
3. Verify drag selection, `Esc` cancellation, primary/secondary displays, mixed scale, and a cross-display rectangle.
4. Verify denial produces instructions rather than a crash.
5. After permission grant and relaunch, verify PNG recognition, result copy, and temporary-file cleanup.
6. Trigger repeatedly during an active selection; only one overlay may exist.
7. Quit and verify the shortcut is no longer registered.

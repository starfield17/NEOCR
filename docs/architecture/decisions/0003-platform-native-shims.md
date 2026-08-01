# ADR 0003: Small native platform shims

Status: accepted

Core owns platform ports. A small per-platform adapter may use a stable C ABI or operating-system interop. macOS uses an Objective-C++ shim for AppKit, ScreenCaptureKit, and registered hotkeys. Windows will use `RegisterHotKey` and Windows Graphics Capture. Raw keyboard hooks are forbidden.

The first macOS screenshot release requires macOS 15.2 so region capture can use the display-agnostic ScreenCaptureKit API instead of maintaining an older per-display crop path.

The macOS C ABI is version 2. It separates permission preflight/request from capture so only an explicit Screenshot OCR user action can display the system prompt. Capture performs a defensive preflight and returns permission denied without prompting.

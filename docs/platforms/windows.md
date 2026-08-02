# Windows continuation

## Bootstrap

Install Git and the .NET 10 SDK, then use PowerShell:

```powershell
git clone https://github.com/starfield17/NEOCR.git
Set-Location NEOCR
git fetch --all --prune
git switch feature/job-checkpoints
dotnet restore OcrWorkbench.slnx
dotnet build OcrWorkbench.slnx
dotnet test OcrWorkbench.slnx --no-build
```

Read `docs/handoff/CURRENT.md` after checkout. Do not copy macOS native binaries or introduce macOS conditionals into Core.

## Planned adapter

- Implement Core's existing hotkey port with Win32 `RegisterHotKey` and a hidden message window receiving `WM_HOTKEY`; do not install keyboard hooks.
- Implement interactive selection with Avalonia borderless overlays, acquire the selected monitor through Windows Graphics Capture desktop interop, and crop in physical pixels.
- Map DPI and negative monitor coordinates inside the Windows adapter.
- Return the same success/cancel/permission/unsupported/failure result types used by macOS.
- Add `win-x64` first, then `win-arm64`, without changing the worker protocol.

## Windows acceptance

Build and test the complete solution, run batch OCR with the fake worker, register/unregister the shortcut, validate shortcut-conflict errors, select across mixed-DPI monitors, and confirm the macOS dynamic library is never loaded. Packaging remains a later self-contained/MSIX decision.

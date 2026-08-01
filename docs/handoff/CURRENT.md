# Current implementation state

Updated: 2026-08-02 (Asia/Shanghai)

The commit containing this file is the handoff baseline. macOS Screenshot OCR implementation is complete on `feature/macos-screenshot`; interactive permission/display QA remains manual.

## Working capabilities

- .NET 10 solution with shared Contracts/Core.
- SQLite WAL job persistence with compare-and-swap state transitions.
- Out-of-process, framed Protobuf recognizer protocol and package validation.
- Batch-image CLI and Avalonia GUI using the same `JobExecutionService`.
- Atomic plain-text export and deterministic fake worker.
- Core ports for registered hotkeys and interactive screenshots.
- macOS 15.2 Objective-C++ adapter using Carbon hotkey registration, AppKit selection overlays, and ScreenCaptureKit region capture.
- Screenshot OCR GUI action, `Control+Option+O`, persisted plugin location, result copy, one-operation gating, and temporary screenshot cleanup.
- Universal arm64/x86_64 native dylib and an ad-hoc signed `.app` packaging path.
- Explicit Screen Recording permission port and macOS ABI v2; Screenshot OCR requests access before validating the recognizer package.
- Architecture/continuity documentation and dependency/link guards.

## Verification baseline

- `dotnet build OcrWorkbench.slnx --no-restore`: passed with zero warnings and errors.
- `dotnet test OcrWorkbench.slnx --no-build --verbosity minimal`: 21 passed (18 Core, 3 architecture/native ABI).
- `dotnet format OcrWorkbench.slnx --no-restore --verify-no-changes`: passed.
- `dotnet list OcrWorkbench.slnx package --vulnerable --include-transitive`: no known vulnerable packages.
- Fake-worker subprocess CLI E2E: completed one input and produced deterministic text.
- `build/macos/package.sh osx-arm64`: produced an ad-hoc signed bundle; plist, code signature, universal dylib, `@rpath` install name, and GUI launch smoke passed.
- Clean `win-x64` cross-publish: produced the Windows executable without the macOS dylib.
- GitHub Actions run `30708367289` at `c4df14c` passed on macOS 15, Windows 2025, and Ubuntu 24.04.

## Known gaps

- No production OCR worker/model package exists yet.
- The permission-first regression, drag selection, mixed-scale/cross-display coordinates, shortcut conflict, and result copy still require hands-on macOS QA.
- Jobs have no page checkpoints, pause/resume runner, or restart recovery.
- A worker is started per GUI operation; pooling and crash restart are not implemented.
- In-flight worker cancellation is not yet correlation-safe.
- Document sources/exporters and VLM providers are contracts/roadmap only.
- Windows and Linux platform adapters are not implemented.

## Next bounded task

Run the manual checklist in `docs/platforms/macos.md` with the fake worker, recording any coordinate or permission defects. If it passes, begin roadmap milestone 3 with correlation-safe worker cancellation before adding process pooling.

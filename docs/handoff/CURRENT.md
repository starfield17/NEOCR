# Current implementation state

Updated: 2026-08-02 (Asia/Shanghai)

The commit containing this file is the handoff baseline. macOS Screenshot OCR manual QA passed. Worker lifecycle milestone 3A is implemented on `feature/worker-lifecycle`, stacked on `feature/macos-screenshot`.

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
- Correlation-routed stdout, serialized frame writes, cooperative cancellation drain, and a two-second forced-termination fallback.
- App-lifetime GUI recognizer sessions reuse one healthy single-flight worker across batch and screenshot OCR; failed workers are replaced on the next operation without retrying the failed task.
- GUI cancellation, app-wide operation gating, deterministic shutdown, and CLI `Ctrl+C` exit code 130.
- Architecture/continuity documentation and dependency/link guards.

## Verification baseline

- `dotnet build OcrWorkbench.slnx --no-restore`: passed with zero warnings and errors.
- `dotnet test OcrWorkbench.slnx --no-build --verbosity minimal`: 29 passed (26 Core, 3 architecture/native ABI).
- `dotnet format OcrWorkbench.slnx --no-restore --verify-no-changes`: passed.
- `dotnet list OcrWorkbench.slnx package --vulnerable --include-transitive`: no known vulnerable packages.
- Fake-worker subprocess CLI E2E: completed one input and produced deterministic text.
- `build/macos/package.sh osx-arm64`: produced an ad-hoc signed bundle; plist, code signature, universal dylib, `@rpath` install name, and GUI launch smoke passed.
- Clean `win-x64` cross-publish: produced the Windows executable without the macOS dylib.
- GitHub Actions run `30709060966` at `18bbb04` passed on macOS 15, Windows 2025, and Ubuntu 24.04, including the permission-first ABI v2 change.
- GitHub Actions run `30710134575` at `1669497` passed on macOS 15, Windows 2025, and Ubuntu 24.04, including worker lifecycle integration tests.
- Worker lifecycle integration tests cover same-process reuse, cooperative cancellation followed by reuse, forced termination and replacement, crash replacement, and invalid-correlation replacement.
- CLI `Ctrl+C` E2E returned 130, persisted state `Cancelled` (`8`), and left no output artifact.

## Known gaps

- No production OCR worker/model package exists yet.
- Jobs have no page checkpoints, pause/resume runner, or restart recovery.
- Worker reuse is single-flight and has no idle timeout or manual unload control.
- A worker crash fails the current task; replacement is lazy on the next operation and there is no automatic page retry.
- Document sources/exporters and VLM providers are contracts/roadmap only.
- Windows and Linux platform adapters are not implemented.

## Next bounded task

Begin milestone 3B with page checkpoint persistence and a resumable runner. Define the SQLite migration and page identity/result records before adding GUI pause/resume controls or abandoned-job recovery.

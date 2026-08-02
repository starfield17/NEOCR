# Current implementation state

Updated: 2026-08-02 (Asia/Shanghai)

The commit containing this file is the handoff baseline. Milestone 3B is implemented on `feature/job-checkpoints`, stacked on `feature/worker-lifecycle`. macOS Screenshot OCR manual QA passed.

## Working capabilities

- .NET 10 solution with shared Contracts/Core and one kernel used by the Avalonia GUI and image-batch CLI.
- SQLite WAL job persistence at schema version 2 with compare-and-swap state transitions.
- Jobs bind to the exact recognizer package ID and version accepted by the worker handshake.
- Page input snapshots and transient SQLite checkpoints preserve successful and declined OCR results while a job is `Running`, `Pausing`, or `Paused`.
- The serial runner pauses only between pages, resumes without repeating checkpointed pages, validates all input snapshots before resume, and lets final-page completion/export win over a late pause request.
- `Completed`, `CompletedWithErrors`, `Failed`, and `Cancelled` transitions delete all page checkpoint content in the same database transaction. SQLite is not permanent OCR history.
- Existing schema-v1 nonterminal jobs migrate to explicit `Failed` records with `Host.LegacyJobNotResumable`; existing terminal records remain intact.
- Current-batch GUI Pause/Resume and Cancel controls. A paused task blocks new batch and screenshot operations; closing the GUI cancels and cleans that paused task.
- Out-of-process, framed Protobuf recognizer protocol, package validation, deterministic fake worker, atomic plain-text export, and correlated worker logging.
- App-lifetime GUI recognizer sessions reuse one healthy single-flight worker. Cooperative cancellation drains its correlated response; unhealthy workers are terminated and replaced on the next operation without retrying the failed task.
- Core ports for registered hotkeys and interactive screenshots.
- macOS 15.2 Objective-C++ adapter using Carbon hotkey registration, AppKit selection overlays, and ScreenCaptureKit region capture.
- Screenshot OCR GUI action, `Control+Option+O`, permission-first flow, persisted plugin location, result copy, one-operation gating, and temporary screenshot cleanup.
- Universal arm64/x86_64 native dylib and an ad-hoc signed `.app` packaging path.
- GUI cancellation and deterministic shutdown; CLI `Ctrl+C` exits 130.
- Architecture/continuity documentation and dependency/link guards.

## Verification baseline

- `dotnet build OcrWorkbench.slnx --no-restore`: passed with zero warnings and errors.
- `dotnet test OcrWorkbench.slnx --no-build --verbosity minimal`: 40 passed (37 Core, 3 architecture/native ABI).
- `dotnet format OcrWorkbench.slnx --no-restore --verify-no-changes`: passed.
- `dotnet list OcrWorkbench.slnx package --vulnerable --include-transitive`: no known vulnerable packages.
- Checkpoint tests cover pre-claim cancellation, pause before page initialization, cross-store pause/resume, no repeated successful or declined pages, final-page pause precedence, changed-input failure, recognizer mismatch, failure/cancellation cleanup, legacy schema migration, and worker-manifest/handshake mismatch.
- Fake-worker subprocess CLI E2E completed one input, persisted schema v2 and recognizer identity, exported deterministic text, and retained zero terminal page rows.
- CLI pseudo-terminal `Ctrl+C` E2E returned 130, persisted state `Cancelled` (`8`), retained zero page rows, and left no output artifact.
- `build/macos/package.sh osx-arm64` produced an ad-hoc signed bundle; plist identity, deep code signature, universal dylib, `@rpath` install name, and GUI launch/quit smoke passed.
- Clean `win-x64` cross-publish produced `OcrWorkbench.Gui.exe` without a macOS dylib.
- GitHub Actions run `30709060966` at `18bbb04` passed on macOS 15, Windows 2025, and Ubuntu 24.04 for the permission-first ABI v2 change.
- GitHub Actions run `30710134575` at `1669497` passed on macOS 15, Windows 2025, and Ubuntu 24.04 for worker lifecycle integration tests.

## Known gaps

- No production OCR worker/model package exists yet.
- There is no startup reconciliation for jobs abandoned in `Running` or `Pausing`, and no historical/paused task browser. The Core runner can resume a known persisted paused job ID, but the current GUI deliberately cancels its paused task on close.
- Worker reuse is single-flight and has no idle timeout or manual unload control.
- A worker crash fails the current task; replacement is lazy on the next operation and there is no automatic page retry.
- Document sources/exporters and VLM providers are contracts/roadmap only.
- Windows and Linux hotkey/capture adapters are not implemented.

## Next bounded task

Begin milestone 3C with startup reconciliation and persisted task discovery. Define deterministic handling for abandoned `Running`/`Pausing` jobs before adding a GUI task list or changing the current close-cancels-paused policy.

# Current implementation state

Updated: 2026-08-02 (Asia/Shanghai)

The commit containing this file is the handoff baseline. Milestone 3C is implemented on `feature/startup-recovery`, stacked on `feature/job-checkpoints`. macOS Screenshot OCR manual QA passed before this milestone.

## Working capabilities

- .NET 10 solution with shared Contracts/Core and one kernel used by the Avalonia GUI and image-batch CLI.
- SQLite WAL job persistence at schema version 3 with compare-and-swap state transitions.
- Jobs bind to the exact recognizer package ID and version accepted by the worker handshake.
- Each run has a unique ID and renewable 15-second lease. Claims, lease renewal, page initialization/results, pause requests and terminal transitions are fenced by that run ID and lease.
- Page input snapshots and transient SQLite checkpoints preserve successful and declined OCR results while a job is `Running`, `Pausing`, or `Paused`.
- The serial runner pauses only between pages, resumes without repeating checkpointed pages, validates all input snapshots before resume, and lets final-page completion/export win over a late pause request.
- Explicit startup reconciliation moves only expired or lease-less `Running`/`Pausing` jobs to `Paused`. Live GUI/CLI runs are not stolen, and a former owner cannot mutate a recovered or reclaimed task.
- `Completed`, `CompletedWithErrors`, `Failed`, and `Cancelled` transitions delete all page checkpoint content in the same database transaction. SQLite is not permanent OCR history.
- Existing schema-v1 nonterminal jobs migrate to explicit `Failed` records with `Host.LegacyJobNotResumable`; existing terminal records remain intact.
- GUI startup/refresh recovery and a persisted paused-task selector with Resume/Cancel. Paused tasks no longer block new OCR operations and remain recoverable when the GUI closes.
- CLI `jobs list`, `jobs recover`, `jobs resume`, and `jobs cancel` commands; every CLI invocation performs safe startup reconciliation.
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
- `dotnet test OcrWorkbench.slnx --no-build --verbosity minimal`: 48 passed (45 Core, 3 architecture/native ABI).
- `dotnet format OcrWorkbench.slnx --no-restore --verify-no-changes`: passed.
- `dotnet list OcrWorkbench.slnx package --vulnerable --include-transitive`: no known vulnerable packages.
- Recovery tests cover pre-claim cancellation, pause before page initialization, cross-store pause/resume, v2 lease-less migration, exact lease expiry, live-run protection, old-owner fencing after reclaim, heartbeat loss/cancellation races, no repeated successful or declined pages, changed-input failure, recognizer mismatch, and terminal cleanup.
- Fake-worker subprocess CLI E2E completed one input, persisted schema v3 with cleared terminal ownership, exported deterministic text, and retained zero terminal page rows.
- CLI recovery E2E changed an interrupted lease-less run to `Paused`, listed it, resumed it to completion, then cancelled a selected paused record; ownership and checkpoint cleanup invariants held.
- CLI pseudo-terminal `Ctrl+C` E2E returned 130, persisted state `Cancelled` (`8`), retained zero page rows, and left no output artifact.
- `build/macos/package.sh osx-arm64` produced an ad-hoc signed bundle and deep code-sign verification passed. GUI launch was not repeatable from the current non-interactive render session (`Avalonia.Native` render-timer error); use the manual desktop runbook.
- Clean `win-x64` cross-publish produced `OcrWorkbench.Gui.exe` without a macOS dylib.
- GitHub Actions run `30709060966` at `18bbb04` passed on macOS 15, Windows 2025, and Ubuntu 24.04 for the permission-first ABI v2 change.
- GitHub Actions run `30710134575` at `1669497` passed on macOS 15, Windows 2025, and Ubuntu 24.04 for worker lifecycle integration tests.
- GitHub Actions run `30733270865` at `151612e` passed on macOS 15, Windows 2025, and Ubuntu 24.04 for transient checkpoints and pause/resume.
- GitHub Actions run `30735564754` at `c74b4c9` passed on macOS 15, Windows 2025, and Ubuntu 24.04 for Milestone 3C startup recovery and leased execution.

## Known gaps

- No production OCR worker/model package exists yet.
- Milestone 4A in the separate public [`NEOCR-Paddle`](https://github.com/starfield17/NEOCR-Paddle) repository passed all five fixed models at the unchanged common tolerance. GitHub Actions run `30747231341` validated one bundle through C# ONNX Runtime on Linux x64, macOS arm64 and Windows x64. The UVDoc issue was a Paddle oneDNN reference-kernel mismatch, not a Paddle2ONNX defect; the external repository's resolved blocker document is authoritative.
- Atomic export precedes the fenced terminal transition. A hard process crash in that small interval can leave the new output present while the task later recovers as `Paused`; resume re-exports deterministically. No output-path lock was added in 3C.
- Worker reuse is single-flight and has no idle timeout or manual unload control.
- A worker crash fails the current task; replacement is lazy on the next operation and there is no automatic page retry.
- Document sources/exporters and VLM providers are contracts/roadmap only.
- Windows and Linux hotkey/capture adapters are not implemented.

## Next bounded task

Begin the independently versioned Paddle worker/runtime/model implementation in `NEOCR-Paddle`, starting with a CPU detection-and-recognition vertical slice through the existing host protocol before adding the three optional document-enhancement stages. Keep runtime and model manifests independent; the next host schema change is v4 because leases consumed schema v3.

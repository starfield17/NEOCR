# Roadmap

1. **Foundation — complete:** contracts, SQLite job state, worker protocol, fake recognizer, CLI, and GUI shell.
2. **macOS Screenshot OCR — implementation complete, manual QA pending:** registered shortcut, interactive region capture, normal OCR job submission, display/copy, cleanup, and platform runbook.
3. **Worker lifecycle and recovery:** persistent worker reuse, correlation-safe cancellation, process restart, page checkpoints, pause/resume, and abandoned-job recovery.
4. **First-party Paddle distribution:** independently versioned worker, runtime, model inventory, CPU baseline, then CUDA/Vulkan-capable packages where the runtime supports them.
5. **Document OCR:** document-source workers for PDF, XPS, EPUB, MOBI, FB2, and CBZ; ordered pages, selectable extraction modes, searchable/exported documents, and separate document-engine licensing.
6. **VLM APIs:** provider adapters, secret storage, prompt recipes, structured decline/fallback policy, rate-limit handling, and no first-party local VLM inference.
7. **Windows:** native hotkey/capture adapters, Windows packaging, and full manual matrix.
8. **Linux:** portal adapters, packaging, and desktop-environment capability matrix.

Each milestone exits only after automated tests, relevant platform smoke tests, updated architecture/contracts, and an updated handoff document are committed together.

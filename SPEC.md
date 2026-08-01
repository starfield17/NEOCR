# OCR Workbench

Intent: craft and public open source — a maintainable C# OCR workbench inspired by Umi-OCR, designed for long-running coding-agent development.

Done when:

1. A global shortcut can capture a screen region and submit it to an installed OCR runtime.
2. Image and supported document batches survive pause, restart and resume, then export the selected formats.
3. GUI and CLI submit the same `JobSpec`; model, runtime and VLM recipe packages remain independently replaceable.

Delivery: .NET 10 desktop application and CLI; development order macOS, Windows, then Linux.

## Do not build

- N1 Do not support legacy Python plugins, settings, CLI arguments or HTTP endpoints.
- N2 Do not load third-party native or inference libraries into the application process.
- N3 Do not implement a system-wide keyboard logger; register only configured global shortcuts.
- N4 Do not implement first-party local VLM inference; provide API adapters and prompt recipes only.
- N5 Do not build a plugin marketplace, automatic community updates or a claimed security sandbox in v1.
- N6 Do not add QR/barcode features, shutdown/hibernate actions or an HTTP server in v1.
- N7 Do not build distributed scheduling, multi-user support or a cloud service.
- N8 Do not add a project that owns no use case and no contract tests.
- N9 When something missing or broken turns up outside this spec, append it to `Found · Not doing` and continue.

## Prior art

- Umi-OCR supplies the behavioral reference for screenshot, batch image and four document extraction modes.
- Reusing: .NET, Avalonia, Protobuf and optional separately distributed document/runtime workers.
- MuPDF-based workers are separate packages with their own AGPL or commercial licensing; they are not linked into the permissive core.

## Found · Not doing

(append-only)


# NEOCR

A .NET 10 OCR workbench with a shared Avalonia GUI/CLI kernel and out-of-process OCR workers. The repository is named NEOCR; the existing `OcrWorkbench.*` assembly namespace is intentionally retained for now.

The current implementation provides the persisted image-OCR foundation: image paths are sent to a versioned Protobuf worker process, job state is stored in SQLite, and successful spatial results are exported as plain text. The included fake worker validates orchestration and does not perform real OCR.

Start with:

- [Product specification](SPEC.md)
- [Architecture index](docs/architecture/README.md)
- [Current handoff state](docs/handoff/CURRENT.md)
- [macOS development](docs/platforms/macos.md)
- [Windows continuation](docs/platforms/windows.md)

```sh
dotnet build OcrWorkbench.slnx
dotnet run --project src/OcrWorkbench.Cli -- images \
  --plugin workers/OcrWorkbench.FakeWorker/bin/Debug/net10.0 \
  --database artifacts/jobs.db \
  --output artifacts/result.txt image.png
dotnet test OcrWorkbench.slnx
```

Launch the Avalonia shell with:

```sh
dotnet run --project src/OcrWorkbench.Gui
```

On macOS 15.2+, configure a recognizer package and use **Screenshot OCR** or `Control+Option+O`. For stable Screen Recording permission identity, build the local app bundle with `build/macos/package.sh osx-arm64`; see the macOS runbook for the manual permission/display checklist.

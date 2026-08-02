# NEOCR

A .NET 10 OCR workbench with a shared Avalonia GUI/CLI kernel and out-of-process OCR workers. The repository is named NEOCR; the existing `OcrWorkbench.*` assembly namespace is intentionally retained for now.

The current implementation provides the persisted image-OCR foundation: image paths are sent to a reusable, versioned Protobuf worker process, batch jobs pause at page boundaries, and transient SQLite checkpoints allow completed pages to be reused after restart. Renewable run leases prevent GUI/CLI instances from stealing live work, while expired runs are recovered to a selectable paused-task list. Terminal jobs delete checkpoint content after atomic plain-text export. The included fake worker validates orchestration and does not perform real OCR.

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

Persisted task commands use the same default database as the GUI unless `--database` is supplied:

```sh
dotnet run --project src/OcrWorkbench.Cli -- jobs list
dotnet run --project src/OcrWorkbench.Cli -- jobs recover
dotnet run --project src/OcrWorkbench.Cli -- jobs resume <job-id> --plugin <package-directory>
dotnet run --project src/OcrWorkbench.Cli -- jobs cancel <job-id>
```

Launch the Avalonia shell with:

```sh
dotnet run --project src/OcrWorkbench.Gui
```

On macOS 15.2+, configure a recognizer package and use **Screenshot OCR** or `Control+Option+O`. For stable Screen Recording permission identity, build the local app bundle with `build/macos/package.sh osx-arm64`; see the macOS runbook for the manual permission/display checklist.

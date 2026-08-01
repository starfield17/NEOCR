# OcrWorkbench

A .NET 10 OCR workbench with a shared GUI/CLI kernel and out-of-process OCR workers.

The current implementation is the Phase 0 vertical slice: image paths are sent to a Protobuf worker process and exported as plain text.

```sh
dotnet build OcrWorkbench.slnx
dotnet run --project src/OcrWorkbench.Cli -- images \
  --plugin workers/OcrWorkbench.FakeWorker/bin/Debug/net10.0 \
  --database artifacts/jobs.db \
  --output artifacts/result.txt image.png
dotnet test OcrWorkbench.slnx
```

The fake worker deliberately returns deterministic placeholder text; it exists to validate process isolation and contracts, not OCR quality.

The initial Avalonia shell can be launched with:

```sh
dotnet run --project src/OcrWorkbench.Gui
```

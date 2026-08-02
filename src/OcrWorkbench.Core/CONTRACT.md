# Core

Owns OCR use cases, resumable job/page state ports, platform ports and deterministic exporters. It depends only on Contracts. Interactive capture permission is an explicit port so composition roots control when an operating-system prompt may appear.

Batch pause occurs only between pages. A resumed job must use the same recognizer ID/version and unchanged page snapshots. Terminal jobs must discard temporary page results after atomic export or terminal failure/cancellation.

Core does not start processes, access a GUI, call native APIs or reference a concrete runtime. Those behaviors are supplied through its ports.

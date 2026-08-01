# Core

Owns OCR use cases, platform ports and deterministic exporters. It depends only on Contracts. Interactive capture permission is an explicit port so composition roots control when an operating-system prompt may appear.

Core does not start processes, access a GUI, call native APIs or reference a concrete runtime. Those behaviors are supplied through its ports.

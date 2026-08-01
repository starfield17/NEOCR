# Core

Owns OCR use cases, platform ports and deterministic exporters. It depends only on Contracts.

Core does not start processes, access a GUI, call native APIs or reference a concrete runtime. Those behaviors are supplied through its ports.

# Infrastructure

Owns persistence and local artifact storage adapters for Core ports. It depends on Contracts and Core and must not contain GUI, CLI or OCR workflow policy.

SQLite schema migrations, compare-and-swap job transitions, page checkpoint transactions and terminal checkpoint cleanup belong here. OCR page results are recovery buffers for non-terminal jobs, not permanent history.

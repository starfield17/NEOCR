# CLI

Owns command-line parsing and presentation only. It composes Core with infrastructure adapters and must not duplicate OCR workflow rules.

The CLI exposes image submission plus persisted-task list, recovery, resume and cancellation commands. Every invocation runs Core-owned abandoned-task reconciliation after database initialization.

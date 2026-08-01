# GUI

Owns Avalonia presentation and desktop lifecycle only. It composes the same `JobExecutionService` used by CLI and may reference platform adapters, but must not duplicate OCR policy, persistence transitions, native ABI details, or worker protocol code.

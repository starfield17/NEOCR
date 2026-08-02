# GUI

Owns Avalonia presentation and desktop lifecycle only. It composes the same `JobExecutionService` used by CLI and may reference platform adapters, but must not duplicate OCR policy, persistence transitions, native ABI details, or worker protocol code.

The GUI discovers paused tasks through Core, allows one active OCR operation at a time, and does not delete intentionally paused tasks on shutdown.

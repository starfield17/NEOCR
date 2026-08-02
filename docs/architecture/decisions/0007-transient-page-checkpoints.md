# ADR 0007: Transient SQLite page checkpoints

Status: accepted

Non-terminal jobs store ordered input snapshots and succeeded or declined OCR outcomes in SQLite. This data is a recovery buffer, not OCR history. Completed, completed-with-errors, failed and cancelled state transitions delete all page rows in the same transaction; paused jobs retain them.

Pause is cooperative at the application boundary rather than a worker protocol feature. An active page finishes and is committed before `pausing` becomes `paused`. If that page was the final page, export and completion win. Resume skips committed pages, requires unchanged stable input identities, and requires the exact recognizer manifest ID and version recorded with the job.

Schema v2 marks pre-checkpoint non-terminal jobs as `Host.LegacyJobNotResumable`; their `JobSpec` remains for diagnosis. Existing terminal records remain unchanged. ADR 0008 adds leased startup recovery and persisted paused-task discovery without changing the transient-data rule.

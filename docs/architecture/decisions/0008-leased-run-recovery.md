# ADR 0008: Leased and fenced job runs

Status: accepted

GUI and CLI may use the same SQLite database, so process startup cannot safely assume every `running` task is abandoned. A live runner must be distinguishable from a crashed runner, and a delayed former runner must not be able to overwrite recovered state.

Schema v3 gives each claimed run a unique `run_id` and UTC lease expiry. The runner renews its lease every five seconds for a fifteen-second window. Page initialization, page results, pause requests and all terminal transitions require both the current run ID and an unexpired lease. Entering `paused` or a terminal state clears the lease; terminal transitions continue to remove checkpoint rows transactionally.

Recovery is an explicit Core use case invoked by GUI and CLI composition roots after database initialization. It atomically changes only `running` or `pausing` rows with a null or expired lease to `paused`, clears ownership, and retains page checkpoints. Initialization itself never performs recovery. This prevents a newly opened GUI from pausing a live CLI task. Pre-v3 jobs with recognizer identity but no lease are recoverable; older pre-checkpoint jobs remain failed under ADR 0007.

Paused tasks are discoverable and can be resumed with the exact recorded recognizer or cancelled. There is no automatic retry, priority scheduler or permanent OCR-result history. After a hard crash, discovery can take up to the lease window; this bounded delay is preferred to stealing live work.

# ADR 0006: Reusable single-flight workers and cancellation drain

Status: accepted

The GUI keeps one recognizer worker alive for the currently selected plugin package. A package change, application shutdown, process exit, transport failure, protocol fault, or cancellation timeout ends that worker. The CLI continues to own one worker for its process lifetime.

Page processing remains single-flight. Control messages have their own correlation IDs and may be written while a page request is active. A cancelled page must produce both an acknowledgement for its `CancelRequest` and a terminal response for the original correlation ID before the worker is reusable. The host waits two seconds, then terminates an unresponsive process tree.

A crash or forced termination never causes an implicit retry of the current page. The current task fails or is cancelled, and only a later task may start a replacement. This avoids hidden duplicate inference before page checkpoints and explicit retry policy exist.

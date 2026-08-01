# Plugin protocol v1

## Transport and framing

Workers are child processes connected through stdin/stdout. Each message is a four-byte big-endian length prefix followed by a Protobuf `Envelope`. Diagnostic output belongs on stderr; stdout is protocol-only. The host rejects oversized, truncated, malformed, unsolicited, or incorrectly correlated frames.

## Package loading

A package is a directory containing `plugin.json` and an entry point below that directory. Loading validates:

- manifest schema and protocol range;
- host RID compatibility;
- entry-point containment (no path escape);
- plugin kind and declared capabilities;
- explicit license metadata.

Model data and inference runtimes are independently replaceable package concerns even when a first-party distribution places them beside one worker. The host must not infer model compatibility from filenames.

## Lifecycle

1. Host starts the worker with redirected standard streams.
2. Host sends `HelloRequest` with its supported protocol range.
3. Worker returns identity, kind, negotiated version, capabilities, and maximum concurrency.
4. Host sends correlated page requests.
5. Worker returns exactly one succeeded, declined, or failed response for each page request.
6. To cancel a page, the host sends a separately correlated `CancelRequest` naming the target correlation ID. The worker acknowledges the cancel request and still emits one terminal response for the target.
7. The host may reuse the worker only after both responses have been consumed. If either is missing after two seconds, the host terminates that process.
8. The host finally sends a separately correlated shutdown request and expects an acknowledgement.

The host remains single-flight per recognizer worker even if `maximum_concurrency` is greater than one. A GUI session reuses one healthy worker for the selected package. Transport failure, invalid correlation, process exit, or a cancellation timeout invalidates that process; the current page is not retried implicitly, and a later request may start a replacement.

## Compatibility

Protocol additions use new field numbers and preserve old meanings. Existing enum numbers and field numbers are never reused. A breaking semantic change requires protocol v2 and a new compatibility decision record. Manifest validation happens before any executable is launched.

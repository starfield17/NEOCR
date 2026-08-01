# Plugin protocol v1

## Transport and framing

Workers are child processes connected through stdin/stdout. Each message is a four-byte little-endian length prefix followed by a Protobuf `Envelope`. Diagnostic output belongs on stderr; stdout is protocol-only. The host rejects oversized, truncated, malformed, unsolicited, or incorrectly correlated frames.

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
5. Worker returns exactly one succeeded, declined, or failed response for each request.
6. Host may request cooperative cancellation and finally sends shutdown.

The current host is single-flight per worker. Robust in-flight cancellation and persistent worker pooling are explicit roadmap items and must not be claimed as implemented.

## Compatibility

Protocol additions use new field numbers and preserve old meanings. Existing enum numbers and field numbers are never reused. A breaking semantic change requires protocol v2 and a new compatibility decision record. Manifest validation happens before any executable is launched.

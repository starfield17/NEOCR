# ADR 0002: Out-of-process Protobuf workers

Status: accepted

OCR runtimes, document engines, and VLM providers execute outside the host. A versioned length-prefixed Protobuf protocol provides deterministic contracts, dependency isolation, and crash containment. This boundary is not represented as a security sandbox.

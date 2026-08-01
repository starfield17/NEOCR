# Architecture index

This directory contains durable architecture. It describes decisions and invariants, not current task progress.

- [System architecture](system.md): contexts, dependencies, data flow, state, privacy, and licensing boundaries.
- [Plugin protocol v1](plugin-protocol-v1.md): package validation, framing, outcomes, and lifecycle.
- [Roadmap](../roadmap.md): ordered capability milestones and exit criteria.
- [Decision records](decisions/): choices that future sessions must not silently reverse.

Mutable implementation state belongs in [`docs/handoff/CURRENT.md`](../handoff/CURRENT.md). When code and documentation disagree, tests and code describe current behavior; update `CURRENT.md` immediately and resolve durable-document drift in the same change.

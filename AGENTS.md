# Agent working agreement

Read these sources in order before making changes:

1. `SPEC.md`
2. `docs/handoff/CURRENT.md`
3. `docs/architecture/README.md`
4. The `CONTRACT.md` belonging to every module you will edit

- Change only the module that owns the requested behavior.
- Change `Contracts` first when a cross-module wire or domain contract must change.
- Every contract change requires consumer tests in the same commit.
- Native runtimes and network providers must remain out of process.
- Do not add project references that violate a module's `CONTRACT.md`.
- Append out-of-scope discoveries to `SPEC.md`; do not implement them opportunistically.
- Keep durable decisions in architecture/ADR documents and transient progress in `docs/handoff/CURRENT.md`.
- At the end of a session, update `CURRENT.md` with verified commands, known gaps, and the next bounded task.
- Use platform adapters for native APIs. Shared Core code must not load platform libraries.

Required verification:

```sh
dotnet build OcrWorkbench.slnx
dotnet test OcrWorkbench.slnx --no-build
dotnet format OcrWorkbench.slnx --no-restore --verify-no-changes
dotnet list OcrWorkbench.slnx package --vulnerable --include-transitive
```

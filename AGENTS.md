# Agent working agreement

Read `SPEC.md` before making changes.

- Change only the module that owns the requested behavior.
- Change `Contracts` first when a cross-module wire or domain contract must change.
- Every contract change requires consumer tests in the same commit.
- Native runtimes and network providers must remain out of process.
- Do not add project references that violate a module's `CONTRACT.md`.
- Append out-of-scope discoveries to `SPEC.md`; do not implement them opportunistically.

Required verification: `dotnet test OcrWorkbench.slnx`.


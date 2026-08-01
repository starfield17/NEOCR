# Session workflow

## Start

1. Fetch the remote and inspect the current branch, status, and recent log.
2. Read `SPEC.md`, `CURRENT.md`, the architecture index, and affected module contracts.
3. Run the verification commands recorded in `CURRENT.md` before editing.
4. State one bounded deliverable and its acceptance checks.

## During work

- Keep unrelated user changes untouched.
- Add or change a durable decision only through an ADR.
- Update contracts before consumers when a public/wire shape changes.
- Prefer a vertical feature slice over unused abstractions.
- Record discovered but out-of-scope work in `SPEC.md`.

## End

1. Run build, tests, format verification, and vulnerability audit.
2. Perform relevant platform smoke tests and state which checks remain manual.
3. Update `CURRENT.md` with facts, not intentions.
4. Commit a coherent increment and push the active branch.
5. On another machine, clone/fetch the branch and rerun the documented commands before continuing.

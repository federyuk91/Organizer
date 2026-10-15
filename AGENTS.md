# Agent instructions — File Organizer

## Objective
Implement a **Windows 10/11 x64**, low-overhead, headless, per-user file organizer. Deliver working source, tests, example configuration, build/publish scripts and documentation. The specification in this repository is authoritative; do not replace implementation with a plan.

## Source-of-truth map
- `README.md`: user-facing scope, CLI and quick start.
- `REQUIREMENTS.md`: normative stack, architecture, configuration contract and acceptance constraints.
- `FUNCTIONAL.md`: processing algorithms, filesystem safety, edge cases.
- `TEST.md`: test matrix and verification requirements.
- `Config.Example.toml`: representative config; must parse and validate.

Read only the section relevant to the current task unless a dependency requires more. Do not repeat full specs in generated files or chat responses. Requirement IDs (`R-*`) and test IDs (`T-*`) link the docs.

## Working rules
1. Work incrementally: configuration + validation → matching + duplicate policy → safe transfers → watches/queue/reconciliation → CLI/autostart → test/publish.
2. Prefer built-in .NET and Windows APIs, few dependencies, no UI, no database, no elevated permissions for normal use.
3. Data preservation precedes speed. Never overwrite or delete without an explicitly configured and verified action. Fail closed when path safety is uncertain.
4. Keep core components independently testable. No direct watcher dependency in rule or transfer tests.
5. Keep `Config.Example.toml`, parsing code, CLI help and docs synchronized. Do not silently change schema semantics.
6. Use deterministic temporary directories and fakeable clock/filesystem boundaries when useful; never test against a real Downloads/Desktop folder.
7. Build and run tests after meaningful changes. Report actual commands/results and blockers; do not claim unexecuted measurements or tests.
8. Prefer a self-contained Native AOT Release on Windows if feasible; maintain a conventional .NET publish fallback. Do not compromise reliability to force AOT.
9. Avoid speculative abstractions, excessive commentary and token-heavy duplicate explanations. Add brief decisions to `README.md` only when necessary.
10. If the environment is not Windows, implement portable core and tests; clearly mark Windows-only startup/watcher/AOT validation as unverified.

## Completion
The task is complete when the implementation satisfies `REQUIREMENTS.md`, the algorithms and safety invariants of `FUNCTIONAL.md` are enforced, tests from `TEST.md` are implemented and run where supported, and build/publish usage is documented. List verified vs unverified items concisely.

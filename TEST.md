# Test plan / acceptance matrix

Use xUnit or another lightweight mainstream .NET test framework. Tests must operate **only** within temporary fixture directories; never on live Downloads/Desktop. Each integration test cleans up its own artifacts. Use isolated config files, short timeouts and deterministic coordination rather than arbitrary sleeps. Windows-only tests should be conditional and reported as skipped when unsupported.

## Configuration and matching

| ID | Requirement | Acceptance test |
|---|---|---|
| T-01 | R-05/06 | Valid sample TOML parses, expands `%USERPROFILE%`, preserves independent folders; wrong version/unknown enum/missing destination fails clearly. |
| T-02 | R-08/09 | `.ase` routes to `Images/Aseprite`; multiple image extensions to `Images`; unrelated file stays. |
| T-03 | R-09 | Glob `*invoice*.pdf`, `?`, exclusions and case sensitivity; basename-only and anchored matching. |
| T-04 | R-09 | First matching active rule wins; disabled rule/folder ignored; combined extensions AND patterns behavior. |
| T-05 | R-08 | Relative paths resolve against their own source; absolute destination preserved; Unicode paths accepted. |
| T-06 | R-12 | Valid edit updates active policy; invalid/incomplete edit retains last good snapshot. |

## Path validation

| ID | Requirement | Acceptance test |
|---|---|---|
| T-07 | R-13 | Destination identical to own source is rejected. |
| T-08 | R-13 | Destination identical to *different* source is rejected. |
| T-09 | R-13/15 | Aliases via case, separator, `.`, `..` rejected; test junction/symlink identity on supported Windows setup. |
| T-10 | R-11/14/15 | Nested destinations have explicit managed ownership and are skipped by ordinary source traversal; reject overlapping destinations belonging to different monitors. |
| T-11 | R-15/16 | Duplicate monitor paths rejected, self moves and reparse redirection never executed. |

## Collisions and movement

| ID | Requirement | Acceptance test |
|---|---|---|
| T-12 | R-10 | Different content same name yields `_1`, `_2`; retains both contents. |
| T-13 | R-10 | Identical content same name yields `_copy_1`, `_copy_2` when opted in. |
| T-14 | R-10 | Same-size different-hash files are not treated as identical. |
| T-15 | R-10 | `skip` keeps source intact; `delete` only when opted in and verified; default never deletes. |
| T-16 | R-10/16 | Two workers/processes targeting same name never overwrite; collision races retry correctly. |
| T-17 | R-19 | Same-volume move; destination folder auto-created; source removed only on success. |
| T-18 | R-19 | Cross-volume copy/commit/cleanup where available; injected copy error preserves source and avoids visible partial final. |
| T-19 | R-19 | Missing source, locked file, permission denial and unavailable target fail safely with diagnostic. |

## Monitoring, readiness, CLI

| ID | Requirement | Acceptance test |
|---|---|---|
| T-20 | R-17 | Duplicate Created/Renamed events coalesce; only one in-flight processing per source. |
| T-21 | R-17 | Synthetic watcher overflow schedules scoped reconciliation without duplicate moves. |
| T-22 | R-18 | `.crdownload`, `.part`, `.tmp` ignored; final renamed download processed; growing/locked file not moved early. |
| T-23 | R-18 | Readiness timeout leaves original intact; later retry succeeds. |
| T-24 | R-20 | Startup scan toggle and one-shot `scan` obey recursion and exclusions. |
| T-25 | R-20 | `dry_run` creates no folders and moves/deletes nothing. |
| T-26 | R-21 | `validate` exits without moving; help works; invalid config exits nonzero; second worker with the same or another TOML is refused; background run and graceful stop work; old same-config locks remain respected. |
| T-27 | R-21 | Per-user autostart install/uninstall test uses isolated or mock registry/task layer; never changes developer machine implicitly. |
| T-34 | R-14, R-21 | Literal percent characters and environment-looking tokens in existing filenames are preserved; files disappearing during readiness are not reported as timeouts; autorun upgrades preserve config paths and switch to background mode. |
| T-28 | R-22 | Log level filters correctly and rotation limits disk use. |
| T-29 | R-03/23 | Release and Native AOT publish checks on supported Windows toolchain; record real exe size, working set and idle CPU only if measured. |
| T-30 | R-11/12/24 | Add a higher-priority `*gatt*.png` rule after files are in Images: organized and manually placed files move to Images/Cats without extra source definitions. |
| T-31 | R-12/24 | Reorder rules in both directions; edit a destination and reconcile its previous session scope; invalid updates retain the old routing snapshot. |
| T-32 | R-11/17/24 | Watch nested/external managed destinations with a nonrecursive source; handle missing-directory creation and imported directory trees without periodic polling. |
| T-33 | R-10/16/24 | Reconciliation is idempotent under copy/skip/delete; reuse collision policies and reject generated names that change routing; unmatched/temp/excluded files and dry run stay safe. |

## Execution checklist

1. `dotnet restore` / `dotnet build -c Release`.
2. `dotnet test -c Release` with summarized pass/fail/skip counts.
3. Validate `Config.Example.toml` with CLI using test-only safe source paths or a temporary transformed copy.
4. Run integration tests covering real filesystem watcher behavior where supported.
5. `dotnet publish` on Windows (AOT if compatible), verify output launches and `--help`/`validate` work.
6. Report untested Windows-specific cases honestly when environment is not Windows.

**Release gate:** no failing safety/integrity tests; no unexplained skipped required tests. If a feature cannot be validated, document precise limits and do not claim that it passed.

## History and UI synchronization

| ID | Requirements | Verification |
|---|---|---|
| T-35 | R-25, R-26 | Persistent moves, final collision names, exact seven-day cutoff, config filtering/all-config view, metadata-only expiry, chained reverse undo, same-volume and physical cross-volume undo. |
| T-36 | R-25, R-26 | Refuse modified/replaced/missing files and occupied originals; cancellation, prepared/undo-prepared recovery, partial-tail repair, corrupt journal refusal and worker stop on journal failure. |
| T-37 | R-27 | WPF recursive checkbox loads true accurately; unsaved false leaves disk true with a draft banner; save reads false back; clean external true reloads; dirty conflicts retain edits and display current disk values. |
| T-38 | R-27 | Report another active TOML, rejected/unapplied hashes, actual applied folder flags, and stale PID/start identity; four WPF tabs render without binding errors. |

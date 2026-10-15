# Requirements and architecture

## Stack and project boundaries

- **R-01**: C#, latest suitable supported .NET LTS at implementation time; target Windows 10/11 x64. Build portable business logic when possible. Use Native AOT where dependencies and behavior permit; standard self-contained publish must also work.
- **R-02**: Headless CLI/worker. No GUI framework in the engine, Windows Service, cloud service, web server, database or Unity. The independent editor uses TOML and shared safe history/undo services; core must not reference UI projects.
- **R-03**: One per-user background process, asynchronous, minimal allocations, bounded queue/concurrency, event-driven watch rather than recurring directory polling. Per-user autostart via Task Scheduler or HKCU Run, documented and reversible.
- **R-04**: Separate config parsing/validation, path analysis, rule matching, queue/watch management, readiness detection, duplicate resolution, safe movement, startup registration and logging. Favor simple testable types over needless interfaces.

Suggested solution (rename if justified):

```text
src/FileOrganizer/                # CLI and background host
src/FileOrganizer.Core/           # config models, validation, matching, processing
src/FileOrganizer.Windows/        # watchers, startup registration, Win32-specific I/O
 tests/FileOrganizer.Tests/       # unit and safe integration tests
Config.Example.toml
README.md REQUIREMENTS.md FUNCTIONAL.md TEST.md
scripts/build.ps1 scripts/publish.ps1
```

## Config schema (version 1)

- **R-05**: TOML versioned `version = 1`; unknown schema versions, unknown relevant enum values, missing required keys, invalid globs/paths, duplicate monitor identifiers, and unsafe paths fail validation. Provide line/location where parser allows. Do not partially apply bad configs.
- **R-06**: Default config location `%LOCALAPPDATA%\FileOrganizer\config.toml`, override `--config <path>`. Environment variables in source/destination paths expand before validation; nonresolved variables fail validation. Use current user's profile and permissions.
- **R-07**: `[general]`: `scan_on_startup` (bool), `watch_config_changes` (bool), `dry_run` (bool), `log_level` (`error|warning|info|debug`), `file_ready_timeout_seconds` (positive integer), `max_parallel_moves` (positive, small default). Defaults should be safe and documented.
- **R-08**: Each `[[folders]]`: unique `name`, absolute `path` after expansion, `enabled`, `recursive`, `[[folders.rules]]` and `[folders.duplicates]`. Each rule has `name`, `enabled`, `destination`, optional `extensions`, `patterns`, `exclude_patterns`, `case_sensitive` (default false). Paths in `destination` are absolute or resolved against **that monitor's source directory**, never the process working directory. Paths to destination directories may be created on move; watch sources must exist and be accessible.
- **R-09**: First enabled matching rule wins; matches use the file basename (not full path). Within `extensions` OR matching; within `patterns` OR matching; **when both lists are present, both conditions must match** (AND). If neither is present, rule is invalid (prevent catch-all mistakes). Any matching exclusion vetoes that rule. File extensions normalized with leading dot; comparisons default case-insensitive (Windows) and are overrideable per rule. `*` means zero or more characters and `?` one character; anchor against the entire filename. Matching applies only to files, not directories. Unsupported regex is not required.
- **R-10**: Per-folder duplicate policy: `name_collision = "increment" | "skip"`; `identical_content = "copy" | "skip" | "delete"`; `copy_suffix` is a template containing exactly `{n}` and applied before the extension. On same-name collision, compare sizes then SHA-256 only as necessary. Same content is identified when compared candidates have identical bytes by SHA-256; search only colliding filename candidates, not the entire destination. `copy` produces e.g. `image_copy_1.png`; differing content with `increment` produces `image_1.png`. `skip` leaves source unchanged. `delete` is destructive and opt-in only after strict verification. No silent overwrite. File name suffix generation must cope with already-suffixed source names and existing numbered candidates.
- **R-11**: `recursive=false` limits ordinary source traversal to direct children; `recursive=true` includes source descendants. Enabled rule destinations are additional managed scopes, recursively watched and reconciled by their owning folder's ordered rules even when its source is nonrecursive. Exclude those subtrees from ordinary source traversal to avoid double ownership, then reconcile them explicitly. Include manually placed files without tracking provenance; never follow junction/symlink directory chains.
- **R-12**: Config changes are debounced and validated as a full candidate; apply atomically to watchers/rules, retaining the last good config on invalid updates. Reconcile source and managed destination scopes after rule additions, edits, disable/removal or reordering. Retain prior destinations for the same canonical source during the worker session so changing a destination can drain its previous directory. In-flight operations use one consistent immutable routing/configuration snapshot; obsolete queued work is rescheduled with current rules. Config-file watch must not cause self-processing.

## Path safety

- **R-13**: Globally compare every **enabled rule destination** against every **enabled monitored source**, across all folder definitions. Reject any exact source/destination equivalence, including aliases detectable via canonicalization, case, separators, `.`, `..`, junctions/symlinks and (where possible) final filesystem identity. Do not assume lexical differences imply different directories.
- **R-14**: A destination nested under a source has explicit managed ownership and is excluded from that source's ordinary traversal. Destinations belonging to different enabled monitors must not overlap (equal, ancestor or descendant), even if the sources do not overlap. Reject ambiguous ownership/cycles, including retained session scopes. Nesting destinations belonging to the same monitor is allowed; collapse recursive scan/watch roots where possible.
- **R-15**: Reject monitor paths that duplicate each other (including resolvable aliases). Detect overlapping recursive monitors and prevent duplicate handling; either reject such configurations or implement unambiguous exclusion/ownership. On each move, re-check applicable path safety, including symlink redirection; fail closed if identity cannot be safely resolved.
- **R-16**: Never move a file onto itself or overwrite any existing file, regardless of casing/races. Failed validation cannot cause live-file changes.

## Runtime behavior

- **R-17**: Watcher notifications for create/rename/move into scope. Deduplicate event bursts and serialize handling of the same source. Callback must enqueue only; queue work is bounded. Handle watcher errors, overflow, inaccessible/deleted source and reinstatement. Perform startup snapshot only if requested; reconcile after missed notifications. Never busy-spin.
- **R-18**: Ignore temporary extensions such as `.crdownload`, `.part`, `.tmp` (configurable future expansion). Require file readiness (stability + practical accessibility checks), asynchronous bounded retry and timeout. No claim of definitive knowledge that all writers finished; apply conservative strategy and recheck before move.
- **R-19**: Create target directories on demand; move within same volume without replace; for cross-volume copy to uniquely named temp file in destination, verify bytes/hash, reserve/commit final name without overwrite, then delete source only after success. On failure leave source intact whenever possible and clean only owned temps. Handle disappearing files, permissions, long paths, Unicode and unavailable volumes.
- **R-20**: On `scan` process one existing snapshot and exit; on `run`, optional startup scan then continued monitoring. Files with no matching rules are untouched. `dry_run` logs intended operations but never writes, moves, deletes or creates destination folders.
- **R-21**: CLI: `run`, `scan`, `validate`, `install`, `uninstall`, `stop`, `--help`, with `--config`. `run --background` detaches its console; autorun uses this mode. Clear exit codes (0 success, nonzero config/runtime failures). Autostart is explicit, not an implicit side effect of `run`. A per-user mutex prevents concurrent workers even with different TOML paths; retain the previous config-scoped lock for compatibility with older workers using the same config. `stop` requests cancellation through a per-user event, without polling or terminating an active transfer forcibly.
- **R-22**: Bounded rotating logs under LocalAppData, default warning, no repeated idle spam; diagnostic messages for invalid rule/path, rejected moves, retries and watcher recovery. No unbounded log growth.
- **R-23**: Ship reproducible build/publish scripts, example TOML, tests and usage docs. Report actual metrics only if measured; benchmark idle CPU/RAM/executable size on Windows if environment allows. Do not invent numbers.
- **R-24**: Reconciliation and managed-directory events apply the owning folder's current first matching rule to every regular file, regardless of provenance. A file already directly inside its winning destination is a no-op before readiness/hashing/duplicate handling. Unmatched files remain. Reuse safe transfers and duplicate policies; if a generated collision name would select a different destination, preserve the source and report the routing conflict rather than create a processing loop. Startup/one-shot/recovery scans include managed scopes; healthy operation remains event-driven with no periodic polling or per-file history.

## Example semantics

With source `%USERPROFILE%\Downloads`, `.ase` → `Images\Aseprite`, `.png|.jpg|.tiff|.gif|.bmp` → `Images`, `*invoice*.pdf` → `Documents\Invoices`; when destination `photo.png` already exists with different content, new incoming `photo.png` becomes `photo_1.png`; if content is identical and `identical_content="copy"`, `photo_copy_1.png`. A rule moving into a path equal to any enabled source is invalid even if the destination belongs to another monitor.

## Move history and applied configuration state

- **R-25**: Persist a seven-day per-user movement journal without a database or full-content backups. Flush a prepared record containing source, final destination, size, SHA-256 and filesystem identity before mutation; commit after transfer. Journal failure stops further movement. Retention removes only owned expired metadata. Rule matching must remain independent of history/provenance.
- **R-26**: The GUI supports refreshing, selecting and undoing recent moves, including all configuration paths. Undo stops the worker, acquires the per-user lease, verifies unchanged content/identity and never replaces occupied original paths. Reuse no-replace native movement and cross-volume verification. Process chains newest first, retain completed restores on cancellation, and leave the worker stopped.
- **R-27**: Clearly separate unsaved form edits, verified disk values and the running engine snapshot. Auto-reload clean external edits; preserve dirty drafts with a visible conflict. Report mismatched TOML paths, unapplied hashes and rejected reloads using event-driven process-bound diagnostic state. No unverified form value may be presented as applied engine state.

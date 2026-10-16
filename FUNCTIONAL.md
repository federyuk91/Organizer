# Functional algorithm and safety invariants

This document defines processing flow; see `REQUIREMENTS.md` for config/schema and `TEST.md` for assertions.

## Invariants

- **F-01**: Never overwrite an unrelated existing destination object.
- **F-02**: Never remove the original before a verified, durable-enough target exists (except explicitly chosen identical-content deletion after independent verification).
- **F-03**: Never organize a file outside its unambiguous source/managed-directory ownership or through a forbidden reparse scope. Destinations are excluded only from ordinary source traversal, not from explicit managed reconciliation.
- **F-04**: Every individual move uses one immutable, validated config snapshot.
- **F-05**: Invalid config cannot replace last known good config.
- **F-06**: A file is handled at most once concurrently; events may coalesce, but overflow cannot be silently ignored.
- **F-07**: A dry run never mutates the filesystem, including folder creation or deletion.
- **F-08**: A file directly in its first matching rule's destination is a no-op, including under identical-content `delete`. Every committed collision name must still resolve to that destination.

## Startup / reload

```text
MAIN(args):
  parse CLI; resolve absolute config path
  if help: display usage; exit
  if stop: signal the per-user stop event without loading TOML; exit
  if uninstall: remove ONLY this config's per-user startup entry without loading TOML; exit
  candidate = parse(config); validateSchema(candidate)
  paths = expandEnvironmentAndNormalize(candidate)
  validateAllActiveSourcesAndDestinationsGlobally(paths)
  if invalid: print diagnostics; exit nonzero
  if validate: exit 0
  if install: register the engine with run --background for this validated config; exit
  if run OR scan: acquire per-user instance lease and legacy same-config lock
  initialize movement journal; publish diagnostic state only outside dry run
  initialize bounded logger, queue, workers, watchers (run only)
  if scan OR (run AND scan_on_startup):
    enumerate direct files in each active source/destination/selected path once; descend only where its effective mode is recursive,
    respecting source recursion, ownership and reparse exclusions
    enqueue each once
  if scan: drain finite queue; exit with meaningful error status
  if run: await filesystem/config events until stopped

ON_CONFIG_CHANGE:
  debounce completion of write
  parse + validate whole candidate, including cross-folder path safety
  on failure: log; retain current watchers and immutable rules
  on success: retain prior managed destinations for unchanged canonical sources
              reject ambiguous ownership; build new watches/routing snapshot
              swap atomically; reconcile sources and managed destinations
              dispose obsolete watchers safely; reschedule stale queued work
```

## Watch and process

```text
WATCH_EVENT(file):
  never enqueue directories for transfer; ignore managed-temp extensions
  find its source/managed-directory owner; skip config/log/temp files
  coalesce directory creation/reinstatement into watcher refresh/reconciliation
  enqueue if not queued/inflight (small bounded asynchronous channel)

PROCESS(file, immutableSnapshot):
  lock logical source identity to avoid duplicate concurrent handling
  if path no longer exists OR not a regular file: return
  if excluded, symlink unsafe, or outside owned source/managed scope: return
  rule = first enabled rule matching filename (AND across nonempty
         extensions/patterns sets, OR within each, exclusions override)
  if none: return
  if parent directory == rule.destination: return without hashing/deletion
  ready = await waitForStableAndAccessibleFile(file, timeout, cancellation)
  if !ready: warn; allow future event/reconciliation; return
  destDir = resolveRelativeOrAbsolute(rule.destination)
  revalidate actual path identities; reject cycles/self-target/reparse escapes
  if dry_run: log intended action; return
  ensure safe destDir exists (after validation)
  target = destDir + source basename
  resolve collision policy and move safely
  on transient I/O failure: retry with bounded backoff
  on permanent failure: leave source intact and log
```

## Filename collision algorithm

```text
RESOLVE(source, destDir, policy):
  desired = basename(source)
  if desired unused: transfer(source, desired); return
  if desired is not a regular file: reject (no overwrite)
  identical = sameSize(source, desired) AND sha256(source)==sha256(desired)
  if identical:
    if action == skip: leave source; return
    if action == delete:
      revalidate strict identity and source ownership; delete source; return
    if action == copy:
      for n in 1..:
        candidate = stem(source) + copySuffix(n) + extension(source)
        if candidate unused: transferWithoutReplace(source,candidate); return
        # existing candidate may be different or identical; never replace
  else:
    if name_collision == skip: leave source; return
    for n in 1..:
      candidate = stem(source) + "_" + n + extension(source)
      if candidate unused: transferWithoutReplace(source,candidate); return
  # If another process claimed the candidate in between, retry resolution.
```

Check the actual target identity immediately before transfer. Use platform no-replace operations / creation-exclusive reservations where appropriate; a mere `File.Exists` guard is insufficient. For identical-content `delete`, verify the same source and destination remain unchanged across comparison before deletion; treat failures or file modification as `skip` and log. Ensure rules cannot delete the source in place of its own target.

Before committing a generated collision name, run first-match selection on that basename. If it chooses a different directory (or no rule), reject the operation with the original intact. This makes committed placement a fixed point without persisting file provenance. Managed scopes include descendants only when selected by the effective directory policy. A manually placed file in `Images/Inbox` can be reclassified only if that directory is included recursively or explicitly; `recursive=false` on `Images` alone leaves it untouched.

## Move / copy transaction

```text
TRANSFER(source, finalPath):
  check source still ready; source != finalPath; check path safety
  if same volume:
    perform filesystem move with no replacement, catch destination race
    on race: return to collision resolution
  else:
    create unique temp file in destination dir with CREATE_NEW semantics
    stream source -> temp (bounded buffer); hash/verify bytes as needed
    flush and close temp; ensure source unchanged since copy began
    atomically rename temp -> finalPath WITHOUT REPLACE if supported
    on collision: discard owned temp, retry collision resolution
    after successful verified commit: remove source
    if source removal fails: log preserved duplicate for reconciliation
  never delete source on failed transfer; cleanup only own temp paths
```

Cross-volume operations cannot be wholly atomic. Document crash windows, e.g. verified destination exists but source deletion has not occurred, and ensure recovery does not destroy files. Reject unsafe symlink/reparse substitution and avoid following destination symlinks into monitored/source folders. Validate not only lexical paths but filesystem identity where possible.

## Reconciliation and shutdown

```text
ON_WATCHER_OVERFLOW_OR_RESTART:
  report event; schedule a bounded reconciliation scan of affected scope
  dedupe against queued/inflight work; apply current ownership and exclusions

ON_SHUTDOWN:
  stop new watch notifications; cancel delayed retries
  drain/stop bounded work safely, dispose watchers and release lock
  do not abandon half-written final paths
```

## Not guaranteed

File readiness heuristics cannot prove an arbitrary writer has closed its last handle. Hard links, network shares, reparse points, concurrent external file modifications and power loss introduce platform-specific limits. Fail closed when verification is ambiguous; document residual risks rather than claiming absolute atomicity.

## F-10 — Seven-day move history and undo

Prepare and flush the record only for actual moves (not dry run, already-placed files, skipped collisions or duplicate deletion). Preserve final collision names and stream the source digest while its handle denies modification. A copied target's identity is recorded before commit. Commit metadata after the same safe transfer used by normal processing. A journal failure stops the worker. Only a partial final record with the known writer prefix may be discarded; malformed complete records fail closed.

Undo acquires the per-user instance lease after the GUI requests safe shutdown. Replay current records for seven days, reject changed/replaced/missing destinations and occupied original paths, flush UndoPrepared, transfer without replacement, and append Undone. Undo newest first and refresh predecessor identities for movement chains, including cross-volume copies. Verify interrupted operations by identity and digest before completing their metadata; ambiguous double-copy cases are preserved. No history is used by rule matching.

## F-11 — Configuration display synchronization

The editor keeps a draft and an independently verified disk summary. File events and window activation refresh disk state; clean drafts reload automatically, dirty drafts retain their contents and disable overwrite. After saving, reopen the actual file. The worker publishes atomic diagnostic snapshots for accepted configuration text and rejected reloads. The GUI verifies process start identity and compares text hashes, showing disk and applied recursive flags separately. Unknown/missing state is explicit. History refresh is event-notified and user-triggered; there is no idle polling.

## F-12 — Directory selection and lazy path tree

Resolve each directory with the shared DirectoryScope policy. An excluded ancestor vetoes the entire subtree. Otherwise use the most specific override: recursive inherits to all descendants, direct does not inherit. Without overrides, source and each enabled explicit destination use the folder recursive default. Scan active explicit roots independently, skipping roots during ancestor traversal to avoid duplicates; enforce the same admission on watcher events and immediately before transfers. Prior session destinations obey the same policy and are included in applied diagnostic snapshots.

Tree states are off (gray, no descendant nodes), direct/partial (filled square), and complete recursive (check). Off → direct loads only immediate child names, not descendants, without selecting children. Full/off choices discard descendant overrides; direct also resets descendant selection so future children stay excluded. Left click cycles off/direct/full/off; right click reverses it. A recursive node with exceptions displays partial. The editable tree is a draft; saved and live-process views are read-only and marked stale on changes. No timer enumerates the filesystem. Reparse points are always excluded.

Directories verified to have no subfolders display only off/complete and toggle between those states in either direction. Existing direct selection is complete for current files; future subfolders still follow the saved mode. Detect leaves with a one-level query, on demand, without periodic enumeration. Deduplicate canonical paths across all monitors; nest known roots beneath the shortest configured ancestor and open only configured chains. Connecting directories between active roots/selections of the same monitor include their direct files and appear as direct scan/watch roots. Only their immediate child names are enumerated for the tree; excluded child contents are never explored. Derive connections from configured paths without traversing above an independent external root, respect exclusions and reject ambiguous ownership. A loaded direct parent aggregates to a check if every current child is complete, otherwise a square; propagate child state changes to ancestors. Aggregation does not alter the persisted recursion policy for future children.

## F-13 — GUI scan controls and quick rules

Start scan requires a saved, unchanged, valid TOML and an existing engine executable. Start is enabled only without an active worker; Stop only while a worker/owned scan is active. Confirm saving dirty/new drafts and reuse the validated atomic save (Save As for new files) before launch. Refuse external conflicts; cancelled/failed saves never launch. Recheck the per-user lease after saving to guard concurrent starts. Launch the independent CLI `scan --config <saved-path>` without a console; keep only the last 4096 characters of each output stream. Safe cancellation requests the shared stop event and retains the child process until exit; never kill a transfer. A stop request for an existing background worker does not wait for its exit: refresh verified state before another launch. Neither action changes autorun. Refresh history and disk/runtime state after an owned scan finishes.

Preset selection alone changes no rules. Add appends an editable rule with the chosen name/extensions, blank destination and no patterns/exclusions. New rules retains empty-rule behavior. The fourteen named presets share the ordinary TOML schema and validation; overlapping categories follow existing first-match priority with no automatic reordering.

## F-14 — Alternative WPF views

The window owns one EditorViewModel and the window-scoped services. Classic and Modern contain only view markup/styles and share EditorSurface event adapters. Lazily create and cache views; detach the inactive binding context before attaching the active view. Restore selection after item-source bindings complete, without parsing/applying an unsaved TOML draft or rerunning page actions. Runtime tasks and cancellation remain owned by the same model while switching. Preference persistence is GUI-only, bounded and atomic; failure shows a warning and never rolls back or writes engine configuration.

Modern Manage subfolder operates on the selected source branch through existing direct/recursive/excluded path selections. Confirm before replacing descendant selections; recursive mode includes future descendants, direct mode includes only root files. Preserve external destinations, other monitors and the legacy recursive default. Validate the candidate before changing the draft and reject changes if the draft or selection changed during confirmation. Reflect effective complete recursion in the checkbox and keep changes unsaved until Save; no recursive enumeration is needed to select the branch.

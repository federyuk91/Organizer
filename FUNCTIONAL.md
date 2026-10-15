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
  if install/uninstall: modify ONLY app's per-user startup entry; exit
  candidate = parse(config); validateSchema(candidate)
  paths = expandEnvironmentAndNormalize(candidate)
  validateAllActiveSourcesAndDestinationsGlobally(paths)
  if invalid: print diagnostics; exit nonzero
  if validate: exit 0
  if run: acquire single-instance lock scoped to config path
  initialize bounded logger, queue, workers, watchers (run only)
  if scan OR (run AND scan_on_startup):
    enumerate source files and owned managed destination trees once each,
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

Before committing a generated collision name, run first-match selection on that basename. If it chooses a different directory (or no rule), reject the operation with the original intact. This makes committed placement a fixed point without persisting file provenance. Managed scopes include their descendants, so a manually placed file in `Images/Inbox` can move directly to `Images/Cats` or back to `Images` according to current rules.

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

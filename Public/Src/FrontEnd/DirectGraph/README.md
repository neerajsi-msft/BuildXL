# DirectGraph text construction

Fragments use `directgraph 2`, dense fragment-local `path`, `string` and
`set` tables, and ordered construction records. Unsupported fields are errors.
Native request protocol version 4 supports opt-in automatic temp allocation and
literal source exclusions; its version is independent of the text header.

Processes may specify `tempDirectory $pID` or `enableTempDirectory true`
(`false` leaves allocation disabled). These are mutually exclusive when automatic
allocation is enabled. Neither is a loader default or a process option.
Automatic allocation uses the native per-process unique temp directory, including
native cleanup, untracking and temp environment bindings. Explicit paths must be
unique to their processes. The text loader rejects overlapping explicit temp
claims across projects and overlap with generated files/output-directory roots,
in either declaration order. Use sibling paths or opt-in automatic allocation.

`semaphore $sNAME LIMIT INCREMENT` adds a named native semaphore. Names must be
nonempty and unique within a process; integers are positive decimal values and
the increment cannot exceed the limit. A mutex uses limit and increment `1`.

Source `sealDirectory` records accept repeated `sourceExclusion $sID` fields:
literal nonempty root-relative files/subtrees, not glob patterns. Native seal
construction and matching own their semantics, including strict deepest-root
matching and terminal exclusions.

Explicit file inputs (including executables and copy sources) are bound to
visible actual generated artifacts first. Otherwise they become tracked source
files without needing a covering seal, like DScript's `AddInputFile`. Known
generated/private outputs never fall back to sources. Explicit source files do
not implicitly attach covering seals; directory read grants require explicit
`inputDirectory` dependencies.

Configuration exports and trailing fragment `export` declarations are combined.
Identical artifacts are published once; same-path conflicts (including different
file rewrite counts or directory seal identities) are errors. Re-exporting
dependency artifacts is explicit and does not widen dependency visibility.

## Project construction concurrency

Text projects use the existing `/maxFrontEndConcurrency` setting (processor count
by default; `1` for serial construction). `ProjectGraphToGraphConstructor` runs
ready projects up to that bound. Each project waits for all direct dependencies
to finish publishing exports; unrelated projects do not create tier barriers.
Records/fragments within one project remain strictly ordered. Failed projects
never start their dependents; already-running independent projects may finish.

Each project has its own `DirectGraphTextLoader`, value tables and native
`DirectGraphPipGraphBuilder` stream, including registration IDs and producer maps.
Only ownership checks/claims hold `SharedState.SyncRoot`. Parsing, argument
composition, source existence probes and native pip construction run outside
that lock. Artifact/export tables are concurrent and dependency completion
publishes finalized bindings before consumers run. Native `PipGraph.Builder`
path locks protect graph mutation; construction helpers are per module/symbol
and used in stream order. Shared counters use the existing thread-safe counters.

Explicit source bindings record their consuming project/record. Later explicit
outputs or dynamic directory roots cannot cover those paths: this is the same
unexported-generated-input error as loading the producer first. Configured file
exports are reserved before loading any project. This symmetric active-graph
check does not discover unselected fragments, scan filesystem membership or
require source seals for ordinary explicit inputs.
An ancestor witness index bounds source/output checks without scanning every
source. Unlisted opaque file inputs likewise retain their selected producer
grant so a later sibling's private explicit output cannot bypass the existing
producer-identity check. These indexes track ownership, not source permissions.

Semi-stable hashes, native temp/object paths and fingerprints must not depend on
interleaving. PipIds, table allocation IDs and serialized graph bytes need not
be identical. The external gRPC emitter remains one ordered builder stream.

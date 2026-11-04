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

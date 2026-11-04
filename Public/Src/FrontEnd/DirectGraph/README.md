# DirectGraph text construction

Fragments use `directgraph 2`, dense fragment-local `path`, `string` and
`set` tables, and ordered construction records. Unsupported fields are errors.
Native request protocol version 3 additionally supports opt-in automatic temp
allocation; its version is independent of the text header.

Processes may specify `tempDirectory $pID` or `enableTempDirectory true`
(`false` leaves allocation disabled). These are mutually exclusive when automatic
allocation is enabled. Neither is a loader default or a process option.
Automatic allocation uses the native per-process unique temp directory, including
native cleanup, untracking and temp environment bindings. Explicit paths must be
unique to their processes.

`semaphore $sNAME LIMIT INCREMENT` adds a named native semaphore. Names must be
nonempty and unique within a process; integers are positive decimal values and
the increment cannot exceed the limit. A mutex uses limit and increment `1`.

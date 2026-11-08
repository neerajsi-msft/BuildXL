# DirectGraph text construction

Fragments use only `directgraph 3` (see the version 3 appendix), dense fragment-local `path`, `string` and
`set` tables, and ordered construction records. Unsupported fields are errors.
Native request protocol version 4 supports opt-in automatic temp allocation and
literal source exclusions; its version is independent of the text header.

Processes may specify `tempDirectory pID` or `enableTempDirectory true`
(`false` leaves allocation disabled). These are mutually exclusive when automatic
allocation is enabled. Neither is a loader default or a process option.
Automatic allocation uses the native per-process unique temp directory, including
native cleanup, untracking and temp environment bindings. Explicit paths must be
unique to their processes. The text loader rejects overlapping explicit temp
claims across projects and overlap with generated files/output-directory roots,
in either declaration order. Use sibling paths or opt-in automatic allocation.

`semaphore sNAME LIMIT INCREMENT` adds a named native semaphore. Names must be
nonempty and unique within a process; integers are positive decimal values and
the increment cannot exceed the limit. A mutex uses limit and increment `1`.

Source `sealDirectory` records accept repeated `sourceExclusion sID` fields:
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

## Native construction and transport adapters

`DirectGraphPipConstructor.Construct` is the single construction/validation core.
Its `DirectGraphOperation` subclasses carry `AbsolutePath`, `StringId`, actual
`FileArtifact`/`DirectoryArtifact` values and `PipData`. Process policies, options,
environment/passthrough, semaphores, temp allocation, seal exclusions, compositions
and exports use the same native construction implementation. Results return
produced artifacts, PipId and semi-stable hash directly.

`DirectGraphTextLoader` translates records and binds visible artifacts, then calls
that core. It does not construct protobuf messages, allocate protocol IDs, import
producer IDs, or read artifacts back through a protocol stream. Native argument
data retains the v3 expression layout, chunk boundaries and whole-argument
escaping. Write-file content is likewise native `PipData`.

`DirectGraphPipGraphBuilder` is the ordered protobuf adapter: version checks,
registration/operation IDs, producer-reference resolution, sticky rejection,
`Complete`, wire counters and request/response tracing stay there. It converts
wire strings into `PipData` and calls the same core. The protocol is unchanged:
version 4, with no wire-level PipData field. `DirectGraphValueRegistry` is an
internal example-emitter source under `Examples/DirectGraph/GraphEmitter`, not
a shipped engine type or a text-loader dependency.

Native operation counts/durations come from the core for text and from the
adapter for wire requests (including rejected requests), without double counting.
`Complete` is a wire-only counter. Text verbose logging emits native operation
summaries; gRPC tracing retains protobuf request/response details.

## Project construction concurrency

Text projects use the existing `/maxFrontEndConcurrency` setting (processor count
by default; `1` for serial construction). `ProjectGraphToGraphConstructor` runs
ready projects up to that bound. Each project waits for all direct dependencies
to finish publishing exports; unrelated projects do not create tier barriers.
Records/fragments within one project remain strictly ordered. Failed projects
never start their dependents; already-running independent projects may finish.

Each project has its own ordered `DirectGraphTextLoader`. The native constructor
has no registration tables, operation IDs or producer maps; ownership metadata
uses project/record symbols while artifact references retain actual identity.
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

## Appendix: version 3 framing and deferred PipData

Both the Kati emitter and text loader support only `directgraph 3`. Prior text
versions must be regenerated; no compatibility parser or conversion fallback
remains. All graph and policy fragments use v3. The separate Kati emission-config
grammar remains `buildxlConfig 1`; the protobuf request protocol remains version 4.

Version 3 is UTF-8 text with LF framing and exact structural TAB depths. Every
physical line, including the last, must end in LF. Structural tokens use exactly
one space between tokens, without quoting, backslash decoding, control
characters, blank records or comments. The header is exactly `directgraph 3`;
CRLF framing is not accepted. CR is permitted inside raw scalar payloads.

Registrations are `path ID RAW_PAYLOAD` and `string ID RAW_PAYLOAD`, with dense
fragment-local decimal IDs starting at zero and defined before use. The payload
starts after the second space, on the same physical line. A payload separator
is required even for an empty value. Raw quotes, backslashes and TABs are data;
NUL is rejected.

A scalar at structural depth `d` continues when the next physical line starts
with at least `d + 1` TABs. Each continuation appends a literal LF and strips
exactly `d + 1` TABs, preserving additional TABs as data. A continuation containing
only those TABs represents a trailing literal LF. Dedent ends the scalar.

`set ID` contains `pID` children on following lines exactly one TAB
deeper; regular input fields still
reference a set with `@ID`. Operations and regular typed fields use
TAB indentation, unquoted tokens, and `pID`/`sID`
references without dollar prefixes. Literal-token positions may use an `sID`
reference instead of a bare token. `$pID` and `$sID` aliases are not accepted;
literal dollar signs inside raw registration payloads remain data.

`arguments`, write-file `content`, `environment sNAME`, and process `description`
fields contain exactly one expression starting on the same line as the field.
There is no separate nested scope for the slot's root expression:

| Expression | Meaning |
| --- | --- |
| `sID` | Registered literal string. |
| `pID` | Registered path, retained as a native `AbsolutePath`. |
| `concat [sSEPARATOR] [escapeArguments] :` | Children follow on lines one TAB deeper. |
| `concat [sSEPARATOR] [escapeArguments] - [REFERENCES]` | String/path references follow on this line, separated by single spaces. |

Bracketed portions are optional, not literal syntax. A missing separator means
the empty string. `escapeArguments` renders each immediate child completely,
escapes it as one argument, then joins the escaped values. The markers only
select layout; both forms have identical semantics. Inline lists accept only
references, not nested expressions, and cannot continue on another line.
Raw text appears only in registrations, not inline literal expressions.

For example, the indentation below consists of literal TABs. `<SPACE>` denotes
one literal ASCII space, not a token to emit:

```text
directgraph 3
path 0 out/include directory
string 0 -I
string 1 <SPACE>
process compile
	arguments concat s1 escapeArguments :
		concat - s0 p0
	environment s2 s3
	description concat - s0 p0
```

This illustrates field syntax, not a complete runnable process; the environment
name/value references stand for additional string registrations.
A process has at most one complete `arguments` expression, rooted at `concat`.
Each direct child of an escaping concat is one argument, even when it contains
deeper concatenations. Child rendering rules are respected before escaping the
complete child; separators are not escaped. Empty children remain empty
arguments. The provisional repeated `argument` fields and unary text
`escapeArgument` operator are not supported in version 3. Content, environment,
and process description expressions have no implicit escaping. Environment
values always use this grammar, including singleton `sID` values. Process
descriptions are retained directly as native usage PipData, without flattening
path fragments. Other operations' scalar description metadata remains a string
reference. The earlier separate-line root expression layout is not supported.
A raw single-child concat can be replaced
by its child, but an escaping singleton still performs escaping and must remain.
An empty concat is distinct from a concat containing an empty-string reference.
Expression nesting is limited to 128 structural levels.

Kati groups trailing whitespace with the preceding string chunk, including quote
chunks, without parsing shell syntax or changing any bytes. Leading whitespace
remains separate. This local factoring reduces references without mining global
adjacent patterns; IDs retain insertion order.

Native `PipDataFragmentEscaping.EscapeEachArgument` implements the concat-header
mode directly. `PipDataBuilder.AddEscapedArgument` still wraps its child using
`PipDataFragmentEscaping.EscapeArgument`, independently of concatenation.
Paths expand at rendering time; the wrapper then applies existing C-runtime
argument encoding to the whole result (or uniform common encoding for IPC).
Existing leaf-level `CRuntimeArgumentRules` retain their meaning.

Completed PipData entry buffers can still be interned. Nested children remain
inline spans, not independently shared tree references. Version 3 consumers and
postprocessors must support raw registrations, block dependency sets and expressions.
In particular, an explicit source cannot lie under **any project's shared or
exclusive opaque root**, in either construction order. BoB's broad uniform
private writable roots must therefore not contain source files.
An ancestor witness index bounds source/output checks without scanning every
source. Unlisted opaque file inputs likewise retain their selected producer
grant so a later sibling's private explicit output cannot bypass the existing
producer-identity check. These indexes track ownership, not source permissions.

Semi-stable hashes, native temp/object paths and fingerprints must not depend on
interleaving. PipIds, table allocation IDs and serialized graph bytes need not
be identical. The external gRPC emitter remains one ordered adapter stream.

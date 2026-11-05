# BuildXL and Kati tool-change plan

Status: approved and implemented in BuildXL and Kati, including parallel
project construction under `/maxFrontEndConcurrency` and native PipData-based
construction API extraction. Prior implementation reviews are complete;
the extraction is ready for retained review.
Isolated BoB integration, real execution/cache validation and source-seal
consumer rollout validation remain gated on BoB clearance. Measurement-driven
optimizations and deletions remain optional. This is an internal working plan;
update its main sections directly.

## Scope and constraints

Complete Kati-produced DirectGraph action fragments and BuildXL project linking.
Keep DirectGraph a thin layer over native pip construction APIs and artifact
linking, not a project-policy engine. Do not put BoB orchestration policy into
Kati. Compose shared process fields through Kati's emission config, leaving
policy choices with the caller. BoB integrates the agreed tool contracts
through pregraph generation.

Preserve the production stitched Ninja path used by current ADO SDK builds,
actual artifact identities and rewrite versions, explicit export visibility,
source authorization, and shared construction/validation. Do not introduce a
second independent pip builder.

Kati and other graph-generation tools run as pips in a pregraph BuildXL
invocation. A second BuildXL invocation consumes the generated graph and runs
the actual build.

**Approved construction-concurrency decision (2026-11-04):** the former
serial-only construction constraint is superseded. Independent DirectGraph
projects construct concurrently using the existing `/maxFrontEndConcurrency`
(`IFrontEndConfiguration.MaxFrontEndConcurrency`, with the existing processor-count
default; `1` is fully serial). No DirectGraph-specific flag is added. Reuse the
generic multi-pip project coordinator with bounded dependency-ready scheduling;
the process-only coordinator contract does not represent arbitrary subgraphs.
Every direct dependency must finish construction and export publication before
its dependent starts; records within each project remain strictly ordered.

Keep protocol value registrations, operation IDs and producer maps in each
gRPC adapter stream only. Text projects bind actual native artifacts and
PipData directly, with no protocol-ID bookkeeping. Synchronize only shared ownership checks/claims
and publish finalized artifacts through concurrent tables. Do not hold a global
lock across parsing, argument construction, existence probes or native pip
construction. Native graph mutation continues through the same thread-safe
`PipGraph.Builder` APIs used for parallel DScript evaluation; helpers remain
per module/symbol. Shared counters and resolver export publication must be safe
across projects. In-flight independent work may finish on failure, but failed
projects must not start dependents.

Make the active-graph source/output check symmetric: remember explicit source
bindings with their consumer context, and reject a later explicit output or
dynamic output root covering them with the existing unexported-generated-input
diagnostic class. Configured file exports are reserved before loading begins.
Dependency-exported artifacts bind as generated, not sources. This does not
scan unselected fragments, introduce stale-workspace ownership discovery or
restore a source-seal gate on explicit files.
The rule rejects explicit sources under **any project's dynamic root**, both
shared and exclusive opaques, in either construction order. BoB's broad uniform
private writable roots must not contain sources; calling a root private does not
exempt it from this active-graph ownership check.
Retain an unlisted opaque file input's selected producer grant as well, so a
later sibling's private explicit file cannot evade the same existing
producer-identity check that rejects a private file claimed first.

Stable per-pip hashes, weak/static fingerprints and temp/object/output paths
must survive interleaving; PipIds, table IDs and serialized bytes may vary.
Validate repeated serial-versus-parallel native graphs including independent
projects, diamonds, compositions, export deduplication and both temp modes,
as well as dependency publication barriers, failure propagation and both orders
of source/output conflicts. Retain the external gRPC ordered-stream contract.

**Review follow-up scope (2026-11-04):** retain the inexpensive six-project
serial/parallel determinism fixture. Do not add a P3 overlap assertion, test
hook, overlap counter or special lock instrumentation. Judge concurrency
effectiveness on representative big graphs when separately cleared. Containment
index changes for the remaining ownership scans are deferred pending ordinary
construction timing on such graphs; no lock-contention or speedup claim is made.
Unexpected construction exceptions must stop queued project admission before
being rethrown, and configured-export diagnostics have their own phase context.

**BoB graph generation and integration testing remain deferred to the end and
require explicit clearance.** Do not modify or run another session's existing
graph. Tool development uses focused tests and isolated fixtures first.

## Design status

| Topic | Status | Remaining decisions |
|---|---|---|
| Project process policy and linking | Direction agreed | Kati emission config; detailed validation deferred to implementation, export details below. |
| Environment | Direction agreed, including authoritative passthrough | Same Kati config selects inherited action names; configured passthrough overrides Make's environment decisions for those names. |
| Source seals | Direction agreed; artifact sharing deferred | Consumer validation before rollout and cache replay; revisit artifact sharing only if profiling justifies it. |
| Output ownership | No new machinery required | Clean CI state and a fixed graph exclude the stale-output scenario; retain existing checks. |
| Fragment and export contract | Direction agreed | Config details deferred to implementation; retain existing resolver selection semantics. |
| Coarse action fields | Direction agreed after capability audit | Text temp/semaphore support, source-seal exclusion plumbing; acquisition-only controls conditional on text emission. |
| Efficiency and deletion | Native API extraction completed by user direction | Keep further optimizations measurement-driven and remove only proven-unused code. |

## Tool changes: project policy and artifact linking

**Agreed requirements**

Dependency exports establish import visibility, not blanket dependencies on
every exported artifact. Consumer policy can add project-wide inputs and
explicit shared writable roots. It can also establish or extend the current
project's exports; it cannot change another project's interface or expose its
private outputs.

Do not implement reusable policy as a DirectGraph-specific project-defaults
engine. Existing per-process construction fields express the effective inputs,
outputs and environment. Supply reusable process fields through the selected
Kati emission config without expanding the thin adapter's responsibilities.
Policy fragments remain ordinary DirectGraph artifact-construction operations;
no separate DirectGraph policy language or retroactive pip mutation is selected.

Bind requested paths according to the provider's export type:

- Static/partial-seal members resolve to actual produced file artifacts,
  preserving producer and rewrite identity. Do not attach the entire seal
  solely to resolve one member.
- Unlisted dynamic opaque members require directory dependencies, including
  composite shared opaques. Composite construction and directory dependencies
  are supported by both DirectGraph and native BuildXL. The narrower
  limitation is that core
  `PipGraph.Builder.TryAssertOutputExistenceInOpaqueDirectory` rejects
  assertions on composite shared opaques; do not use that assertion API to
  synthesize a concrete file artifact as a speculative fallback. DScript's
  corresponding assertion path reaches the same core restriction.
- Source-seal imports retain source authorization, source artifacts and the
  corresponding directory dependencies.
- Consumer policy can request whole-directory runtime coverage when action
  prerequisites omit reads, such as a header search tree. This intentionally
  introduces broader scheduling dependencies.

Support both composite shared-opaque exports for dynamic membership and
static/partial seals selecting graph-known produced files. Never enumerate
filesystem contents to discover generated membership, include stale files, or
use a source seal to represent generated outputs.

**BuildXL change plan**

- Keep `DirectGraphTextLoader.CreateProcess` translating explicit process fields
  into native `DirectGraphProcess` operations and the shared native constructor. Do not add
  automatic project-default application as the assumed implementation.
- Resolve artifact references against completed dependency exports and earlier
  local declarations, preserving ordinary construction order and actual
  artifact identity. Bound any further tool changes by the composition
  mechanism selected in the fragment discussion.
- Reuse `ResolveFile` and `DirectoryBinding.Members` for typed import binding;
  `StaticDirectory.TryGetFileArtifact` is the equivalent explicit-member API.
- Resolve policy-selected exports after action construction and before project
  completion. Construct any required output seals or shared-opaque compositions
  from actual artifacts.

**Kati change plan**

Keep emitting evaluated actions and their file prerequisites/outputs. Accept
generic caller-supplied table prefixes and process fields through the single
emission config described below. Do not add dependency-project discovery,
shared-root publication or export-sharing policy to Kati.

**Implementation details and invariants**

Settle config encoding and precise process-field conflict rules during
implementation; these are not remaining design-approval blockers. Apply the
agreed fragment/export contract to duplicate/conflicting artifacts, same-root
ambiguity, explicit shared-root contributors and re-exports. Preserve the
distinctions between visibility, file binding and runtime coverage, and between
publication and writable-root declaration.

Uniform shared `outputDirectory` additions create one directory artifact per
process, not one project-wide artifact or implicit dependencies between writers.
For BoB integration, use broad uniform writable roots for private side outputs,
not as an implicit published SDK view. Any consumed/published content still
needs declared file products or an explicit producer-qualified directory
dependency/composition; a coarse action with a known producer can own a dynamic
published tree. Do not require BoB to parse all Kati action names merely to
recover an implicit aggregate export. This is not a new restriction on native
directory APIs or a loader policy engine. If a real use requires another
composition mechanism, identify it explicitly rather than silently adding one.

**Validation**

Use real pip construction tests for static-member file edges, dynamic composite
directory reads, source authorization, explicit directory coverage, compiler
side-output writes, artifact ambiguity and pre/post-action policy timing.
Cover several process contributors at one writable root: path-only directory
references remain ambiguous, unrelated writers do not become dependencies,
and explicit file/directory edges authorize consumed products.

## Tool changes: environment

**Agreed requirements**

Keep three distinct categories:

| Category | Binding and caching |
|---|---|
| Pregraph evaluation values | Declared inputs and tracked environment of generation pips govern regeneration. |
| Tracked action values | Concrete runtime values participate in main-build action fingerprints. |
| Action passthrough names | Values come from the main BuildXL invocation, not captured pregraph values; values do not participate in action fingerprints. |

Keep evaluation and action environment roles distinct. Curate the pregraph pip
environment for Make evaluation; use name-only selection metadata in the same
Kati emission config to choose the inherited action-environment baseline.
Evaluation-only inputs must not enter every action merely because they were
present in Kati's environment. An evaluation-only change can rerun Kati without
invalidating downstream actions whose definitions remain unchanged. Values
expanded into commands or explicitly exported by Make still affect actions.
No separate graph-regeneration mechanism is needed.

Required initial passthrough controls are `NJOBS` and `MAKE_PARALL`. They tune
parallelism/resource use, not output contents. Changing them must neither
invalidate reusable actions nor force execution on cache hits. Output-affecting
values remain tracked.

Passthrough uses must remain late-bound, for example `$$NJOBS` in a Make recipe.
Runtime passthrough cannot change `$(NJOBS)` already expanded into a command
during pregraph generation. Genuine graph-evaluation uses remain tracked.

**Kati change plan**

Keep the evaluation environment controlled at invocation. Extend the planned
emission config with name-only inherited-action selection and passthrough
metadata, rather than adding a second config or an environment-value override
API. This explicitly revises the earlier no-filter direction.

For names not configured as passthrough, select the inherited action baseline
from `BaseEnvironment`, then apply the existing scoped `Environment`
export/unexport handling. Explicit Make exports can add names outside that
baseline; unexports remove them. Do not filter the evaluation environment.
Continue excluding credentials and unrelated session state before launching Kati.

Do not inherit `TEMP`, `TMP` or `TMPDIR` from the Kati invocation into the
action baseline: they describe the pregraph pip's own temp environment.
Reject selecting them for inherited action environment rather than capturing
an invocation-local path in every action. Native process temp construction
owns action temp paths and bindings; do not copy the pregraph temp binding as
a substitute for explicit/automatic action temp settings.

Supply concrete environment additions before Make evaluation through that
curated invocation environment. If generic per-process field additions are
introduced, they must not blindly inject tracked environment or passthrough
rows. Dedicated passthrough-name metadata is the explicit exception to Make
environment precedence described below; ordinary names retain Make semantics.

Accept an explicit list of passthrough variable names as exporter metadata in
that same config. Initial names are `NJOBS` and `MAKE_PARALL`. Every configured
name emits the existing `passthroughEnvironment` field regardless of whether
Kati inherits, assigns, exports, unexports or has no value for it. This is an
intentional user-selected override of Make's recipe-environment decisions for
those names, replacing the earlier proposal that exports/unexports win.

Emit no concrete tracked environment entry or generation-time fallback for
such a name, and do not require a concrete evaluated export value solely to
populate its emitted environment. Resolve the runtime value using native
BuildXL passthrough semantics, including native behavior when no value exists.
Avoid tracked/passthrough duplicate entries.

This override affects emitted action environment only. Make evaluation and
values already expanded into commands or graph structure are unchanged and
remain tracked through those definitions. Configuring main-build passthrough
does not implicitly configure passthrough on the pregraph pip; concrete values
supplied for Kati evaluation remain ordinary tracked evaluation inputs.

**BuildXL change plan**

- Preserve concrete tracked environments emitted in action fragments and the
  existing per-process passthrough representation. Consume Kati's effective
  environment without adding a DirectGraph-specific defaults engine. The
  emitter, not the loader, applies configured passthrough-name precedence.
- Use BuildXL's passthrough environment mechanism for opted-in names. Keep this
  distinct from concrete tracked inheritance.

**Validation**

Test tracked runtime invalidation, passthrough cache stability and runtime value
binding and absence of unrelated ambient values from fragments. For ordinary
names, cover inherited-name selection followed by Make exports/unexports,
explicit exports outside that selection and non-exported reassignment. For
configured passthrough names, cover missing values, explicit exports/unexports,
assignments and target scopes: all still emit only the passthrough name.
Use isolated two-stage fixtures to distinguish pregraph regeneration from
downstream action invalidation and verify expanded command text remains tracked.

**Coarse-wrapper job-control audit**

`bob` computes concrete `NJOBS`, `NLOAD` and the derived `MAKE_PARALL` string.
`bob_plan.execution_plan` captures those values in step environments, but keeps
shell placeholders in command arguments. `projectdb.Package` deliberately
does not substitute ordinary shell build/install commands;
`bob_build.make_package_command_list` wraps them without expanding job controls.
The `Template.safe_substitute` in `bob_jobq.logged_cmd_async_start` is for
logging, not the command passed to the shell.

`bob_ninja.package_instruction` embeds the step records into generated Python.
`bob_package_wrapper.execute` rebuilds each child environment from those stored
values, not its parent process environment. Main coarse instructions do not
provide its optional `host_environment` overlay. Consequently merely marking
the outer pip's job-control names passthrough does not update the child values.
Captured values also remain in generated action inputs until normalized out.

An isolated probe exercised the existing wrapper with fresh temporary
directories and a shell command retaining `${NJOBS}`/`${MAKE_PARALL}`.
Captured `4`/`-j4` won despite parent values `13`/`-j13`; supplying the existing
explicit `host_environment` overlay made that same command see `13`/`-j13`.
Both assertions passed. No repository code was changed, no BuildXL graph was
generated or run, and this did not test cache behavior.

No general command-string rewriting is needed for these ordinary shell
placeholders. The deferred BoB integration must normalize configured passthrough
values out of action-side serialized environments and forward only declared
runtime names into each child. Runtime absence must remove any stale captured
value, not fall back to it. Preserve concrete tracked inputs where Kati or other
generation actually needs them.

`MAKE_PARALL` is constructed before execution, so changing runtime `NJOBS`
does not automatically recompute it. Supply the intended runtime controls
consistently rather than silently deriving a new policy in the wrapper.
Some coarse Ninja commands also directly reference `NLOAD`; leave it tracked
unless separately selected for passthrough.

## Tool changes: source-seal semantics and sharing

**Requirements**

Use strict deepest-root matching: a narrower source seal controls access under
its root, with no fallback to a broader parent's patterns. Retain this behavior
in `SourceSealDirectoryIndex.Match`. Preserve precise exclusions and source
authorization through fresh construction, graph serialization/reload,
incremental scheduling and cache replay.

Retain these exclusion rules:

- Entries are literal root-relative files/subtrees. Excluding `.git` does not
  implicitly exclude every nested repository's `.git`.
- Exclusions are terminal across source-seal grants attached to a process.
  Reject source-seal roots at or below an exclusion during graph construction.
- An explicit tracked file dependency can authorize a particular excluded file.
  Exclusions restrict implicit source grants; they are not a global denylist.
- Permit existence/type probes of the exact excluded boundary when classified
  as probes, but not reads/enumeration or descendant probes through the seal.
  Parent enumeration can observe the excluded name; exclusions do not hide
  filesystem entries.

Preserve `allowUndeclaredSourceReads` semantics unchanged. Source-seal matching
classifies the access first; an enabled undeclared-read allowance can then
authorize a read not granted by seals, including excluded paths. Keep existing
allowed-scope/path/regex restrictions, dynamic input fingerprinting and
static/dynamic producer checks, including cache-replay validation. The flag
does not replace dependencies on generated output files/directories or grant
write permission. Do not make source-seal exclusions a global denylist or
silently disable the flag on existing callers.

**Baseline and compatibility gate**

Verified against upstream merge-base `aab9f814e31a`: `ObservedInputProcessor`
tried top-only and recursive source seals until any seal's `Contains` granted
access. Policies were additive; a nested seal did not revoke a parent's grant.
For example, recursive `/src` allowing everything still authorized
`/src/component/file.cc` when `/src/component` allowed only `*.h`.

Commit `34803786e6` replaced that scan with deepest-root matching. In the example,
the new source-seal policy does not grant access to `file.cc`; the eventual
access result also depends on other declared dependencies and permitted
undeclared reads. This is a semantic tightening, not merely an indexing
optimization.

Strictness is the selected target, not an unresolved choice between deepest-root
and additive matching. Consumer validation is a release gate, not evidence
already obtained. Address consumer assumptions explicitly rather than silently
restoring parent fallback.

`review-validate-source-seal-consumers` owns this gate. First inventory attached
source policies per process, especially nested differing policies and same-root
policies that differ across projects; root counts alone do not establish
equivalence. Then, after explicit clearance, compare representative existing
consumers under baseline additive and strict matching with controlled inputs,
fresh execution and cache replay. Record the binaries, policy/configuration
and cache provenance, and distinguish policy regressions from explicit-file
grants, permitted undeclared reads and unrelated build failures.

The strict behavior is already committed on this branch; there is no runtime
compatibility switch implied by this gate. The retained reviewer also observed
the main BuildXL checkout at `34803786e6`, with the scheduler using the new
source-seal index. The deployed binary was not verified: the current SDK
consumer may already be running strict semantics. A future-promotion hold
alone therefore cannot protect that consumer. Establish actual executable
provenance at the cleared validation stage, and deliberately select a binary
with pre-`34803786e6` additive behavior for the baseline comparison; do not
assume the current main checkout is that baseline.

Do not promote this branch's binaries, including its stitched Ninja path, as a
validated replacement for existing consumers until the gate passes or the user
explicitly accepts the documented compatibility change. Any already-running
strict consumer builds provide evidence only for their actual configuration,
not universal baseline compatibility. This does not block isolated tool
fixtures. Real consumer graph generation/execution still requires separate BoB
clearance; do not interfere with the other session's running build.

The baseline source-seal scheduler suites inspected did not contain a direct
parent-plus-narrower-child additive-grant test. Existing coverage checks
individual seal patterns, recursive/top-only access, and explicit file inputs
overlapping a seal. Our `DeepestSourceSealControlsObservedReads` and
`IndexUsesDeepestRootAndTerminalLiteralExclusions` tests were added with the new
semantics; they do not establish baseline compatibility.

**BuildXL change plan**

- Retain deepest-root/no-parent-fallback matching, the agreed exclusion rules
  and existing undeclared-read authorization. Define consumer validation before
  rollout; do not change access grants as an indexing optimization.
- Keep existing `SourceSealDirectoryIndex.Cache` execution-policy sharing and
  per-project seal reuse. Defer cross-project seal-pip/artifact deduplication
  unless profiling shows worthwhile construction or serialized-graph overhead.
  It is not a correctness requirement or an integration prerequisite.
- If artifact sharing is revisited, require matching complete policies (root,
  recursive/top-only mode, patterns and exclusions), preserve provenance and
  dependency edges, and do not broaden policies to increase sharing.
- Measure exclusion-bearing seal scans in
  `GraphAgnosticIncrementalSchedulingState.IsReadOnlyDirectory`; any root/ancestor
  index must preserve conservative any-cover semantics, not copy the path
  authorization index's deepest-root rule.
- Preserve conservative source/output overlap checks unless a separate semantic
  change is explicitly approved. Review serialization/fingerprint compatibility.
- Carry literal exclusions from the text reader through the versioned request
  and native seal construction call. Keep source-policy matching in the shared
  engine, not an independent authorization algorithm in the text loader.
  Directory bindings need only metadata required for artifact resolution;
  do not duplicate exclusion policy merely to veto explicit source inputs.

Do not confuse an implicit seal grant with an explicitly declared file input.
A Make prerequisite emitted as a tracked `input` is an explicit file
dependency; it can authorize an excluded source such as `.git/HEAD`, subject
to existing generated-artifact/producer checks. This is the already selected
explicit-file exception, not a seal denial bypass to prohibit. Test both cases:
an unlisted read under an exclusion is not granted by seals, while an explicit
tracked prerequisite can grant that file. Keep undeclared-read allowances and
conservative root-based source/output overlap checks unchanged.

**Agreed loader source-input contract: match DScript**

An explicitly declared source-file input does not require covering source-seal
visibility or matching seal patterns, top-only scope or exclusions.
DScript's `AmbientTransformer.ProcessImplicitDependency` adds a file directly
through `ProcessBuilder.AddInputFile`; DirectGraph must follow that native
construction model rather than invent a project source-authorization rule.

Remove `DirectoryBinding.AllowsSource` as a gate on explicit source-file
resolution in `DirectGraphTextLoader.ResolveFile`. For that source-file
resolution, do not automatically attach covering source seals and thereby
broaden implicit read permissions. Source directory dependencies remain
explicit construction fields; their implicit read grants are evaluated by the
shared engine under the selected source-seal semantics.

Preserve actual-artifact binding and existing generated-output ownership,
dependency-export visibility, rewrite and ambiguity checks. A known generated
output must not become a source merely because its producer is unavailable.
Normal native graph/mount validation and source/output conflict checks remain.
This changes source-file acceptance, not generated-artifact export visibility.

Update `UnresolvedInputsRequireMatchingSourceAuthorization` to the new
contract: explicit tracked sources work with no covering seal and when a seal
does not match their pattern/depth or excludes them. Verify they do not
silently import broader seal permissions, and retain tests rejecting
generated/unexported source fallbacks.

Narrower repository/project source policy, if desired, belongs in checks on
the constructed graph or Kati/BoB extraction, not the DirectGraph text loader.
This decision does not require a new policy validator or move source-seal
matching into Kati.

**Kati impact**

No source-seal matching policy belongs in Kati. Continue emitting evaluated
prerequisites. BuildXL binds generated artifacts to their providers and
constructs explicit source dependencies directly; the engine evaluates
implicit directory grants separately.

**Validation**

Cover broad/nested policies, patterns, top-only seals and terminal exclusions.
Populate caches before narrowing policy, then test replay and graph reload so
cached path sets cannot bypass changed authorization. Include the old additive
versus new deepest-root example explicitly.

At the cleared integration stage, compare representative existing product builds
under baseline and stricter matching, including fresh execution and cache replay.
Identify accesses that previously depended on a broader parent seal; distinguish
real stricter-policy failures from accesses authorized by other dependencies or
undeclared-read allowances. Passing synthetic tests alone does not establish
product compatibility. No product-build compatibility run has been performed.

## Output ownership: retain existing behavior

The proposed additional protection concerned stale outputs from BoB projects
whose producers are absent from the active BuildXL graph. CI starts clean and
uses a fixed graph, so protection against leftovers from a different graph is
out of scope.

No new generated-region registry, source-fallback classification or special
prebuilt-provider abstraction is required in Kati, BuildXL or BoB. Keep existing
producer/artifact identity, duplicate-output, dependency-export visibility and
configured-output checks. Continue authorizing intentional sources through
existing source policy and representing acquisition outputs as produced
artifacts.

Do not open unselected action fragments to discover private outputs or scan
the filesystem for ownership. Dirty-workspace switching between different
graphs is not a new guarantee of this work. Existing tests remain relevant;
do not add implementation or tests solely for the deferred scenario.

## Tool changes: fragment and export contract

**Architectural constraint and selected composition boundary**

Keep the DirectGraph layer thin over native pip construction. Typed per-project
defaults applied by the loader are not the selected design: they would make
the adapter interpret orchestration policy. Existing per-process
`inputDirectory`, `outputDirectory` and `passthroughEnvironment` fields, and
their native builder handling, already provide the underlying capabilities.
No new protobuf operation or independent builder is justified for those fields.

Use a single Kati emission config containing caller-supplied string, path and
set table prefixes plus additional process fields. Kati emits the supplied
prefixes itself and derives each generated-ID base independently from that
table's prefix length. Keep supplied IDs stable and tables dense within one
complete logical fragment. Do not require caller-side concatenation, explicit
bias arguments or cross-file table references.

Apply the configured fields to every emitted process, not generated-script
`writeFile` records. Reject conflicting declarations rather than silently
overriding them; exact additive/singleton validation and config encoding are
explicitly deferred to implementation, not separate design-approval blockers.
Policy meaning belongs to the caller, not Kati or DirectGraph.
No new graph-rewriting tool is required.

Initial additions cover additive `input`, `orderOnly`, `inputDirectory`,
`outputDirectory`, `option`, `semaphore`, and explicit/automatic temp settings.
Kati still owns its executable, command arguments, output-file products and
rewrites; additions must not replace or append to those fields. Raw
`environment` and `passthroughEnvironment` rows are rejected: dedicated
inheritance/passthrough metadata implements the agreed environment precedence.
Reject unsupported fields explicitly. Exact encoding, option names, row
arity and duplicate/conflict diagnostics remain implementation details.

Because additions apply to every generated process, artifact prerequisites
must already be available from visible generated-artifact providers, earlier
fragments or explicit source files, not be products of this Kati-generated fragment.
Otherwise the producer receives its own prerequisite or an earlier process
references a later producer. Likewise, do not add an input directory pointing
at the uniform writable root produced by these same processes. Reject those
cases explicitly; add a focused self-dependency/forward-reference rejection
test. Diagnostic wording remains an implementation detail.

Keep the config structured so future support for other pip kinds can be added,
but implement only process additions now. Do not introduce generic pip mutation
or speculative support for other operations. Environment additions still enter
through Kati's invocation and Make evaluation, not blind field injection;
passthrough-name metadata follows the environment contract above.

Keep `graphFiles` and trailing `policyFiles` as ordered ordinary DirectGraph
fragments. An initial `graphFiles` entry can construct source/input seals,
followed by Kati/coarse actions; trailing policy can construct output seals,
compositions and exports. Do not add phase fields or a declaration prepass
solely to express ordering already available in these lists.

**Required lifecycle**

1. Establish provider visibility, additional input policy, writable roots and
   effective environment policy before constructing affected processes.
2. Construct ordered action fragments.
3. Resolve exports and construct output seals/compositions against actual
   artifacts before completing the project and scheduling consumers.

**Agreed export contract**

Keep both project-configuration exports and fragment `export` records. Combine
their declarations, deduplicate identical artifacts and reject conflicting
identities. Dependency visibility does not imply publication: re-exporting
dependency artifacts must remain explicit.

Represent curated output subsets with ordinary seals/compositions over
graph-known artifacts. Do not introduce a new export-selection language or
discover generated membership by scanning the filesystem. Preserve actual
producer and rewrite identities.

For shared output roots, select directory artifacts by their intended producer;
reject ambiguous path-only references. Use explicit `composeSharedOpaque`
operations when consumers need the combined contents of selected contributors.
Do not automatically merge all producers sharing a root or discover/import
otherwise invisible projects. Preserve the existing producer-qualified
reference and composition mechanisms rather than adding new machinery.

**BuildXL change plan**

- Consume the explicit records produced by Kati's configured emission, without
  adding loader defaults. Preserve direct operation-to-native-API translation.
  Resolve artifact references only from available providers/earlier operations;
  do not silently reorder fragments or allow trailing policy to amend processes.
- Implement identical-artifact deduplication across configuration/fragment
  exports as new work, preserving artifact identity and explicit re-exports:
  current loader publication and resolver export registration reject duplicates.
  Deduplicate before emitting `AddExport`, using complete file-artifact identity
  including rewrite count and directory-artifact identity. Keep both
  `DirectGraphTextLoader.PublishFile`/`PublishDirectory` and
  `DirectGraphResolver.TryRegisterExport` consistent; conflicting identities
  remain errors. Track this under `review-implement-export-merge`.
- Construct curated memberships using existing seals/compositions over
  graph-known artifacts and explicitly selected shared-root contributors.
  Preserve ambiguity errors and dependency visibility; do not add filesystem
  discovery, automatic same-root merging or a separate export-selection language.
- Keep authoritative passthrough-name selection in Kati's emission config,
  overriding Make environment decisions only for configured names. Reuse
  existing per-process encoding and duplicate-name validation; confirm native
  missing-value behavior rather than adding loader policy. Kati's baseline
  comes from its controlled invocation, not a fragment-side baseline API.
- Keep the text header at `directgraph 2` for additive field exposure: old
  readers reject unsupported fields rather than silently dropping them.
  Bump `DirectGraphProtocol.Version` for request changes as required by that
  contract; the two version mechanisms are distinct.
- Retain existing resolver root-selection semantics and structural validation.
  BoB owns which roots to request and whether an empty requested build is
  acceptable. Do not introduce a new DirectGraph empty-selection policy or
  silently reinterpret empty roots as selecting everything. Continue loading
  only the selected dependency closure.

**Kati change plan**

Implement the single-config prefix/process-additions interface while preserving
efficient interning, dense table IDs and existing format semantics. Declare the
config as a pregraph pip input. Keep evaluation curation at invocation; add only
the agreed action-inheritance and authoritative passthrough metadata. Do not
require Kati to select project closures, infer providers or encode BoB-specific
policy.

**Validation**

Add focused reader/loader and project coordinator cases for phase ordering,
cross-fragment references, duplicate/conflicting exports, environment
composition and selection behavior. Preserve existing external gRPC support.
Add focused Kati cases for independent prefix lengths, stable supplied IDs,
valid set references, generated IDs after prefixes, process-only additions and
conflict diagnostics. Validate the complete emitted fragment with the existing
reader and native construction path. Include additions referencing a seal from
an earlier `graphFiles` fragment and a producer-qualified dependency directory:
artifact references cross fragments, but table IDs never do. Include the
two-stage evaluation-only and authoritative-passthrough cases above.

## Tool changes: coarse actions and Make compatibility

**BuildXL plan**

Use the source-inspection audit below to bound additions. Expose only required
fields, reuse shared builder validation, and do not port the existing DScript
pregraph merely to use DirectGraph. These are migration capability gaps, not
claims that the current DScript/Ninja pipeline failed.

**Capability audit**

Inspected the current `bob_plan.execution_plan`, `pregraph.bootstrap` worker
construction, `bob_ninja` coarse/package/staging generators and
`bob_package_wrapper` execution/publication paths. Compared their declarations
with `DirectGraphTextLoader.CreateProcess`, the seal operation,
`DirectGraph.proto` and `DirectGraphPipGraphBuilder`. This was source inspection,
not graph generation, sandboxed execution or cache validation.

| Requirement | Existing use / evidence | DirectGraph coverage |
|---|---|---|
| Coarse worker invocation, tracked files/directories/environment, source patterns, shared/exclusive output directories, actual-artifact exports | `bob_ninja.package_instruction`, `packaging_project`, `source_copy_projects`, `_generate`; wrapper executes ordinary Python/shell workers | Existing text/native operations cover the basic shape. No special install, archive, copy-tree or symlink pip is required; workers do that work. |
| Pip-local scratch/temp | `bob_package_wrapper.package_scratch`, `run_package`, `package_payload`; Ninja calls `ProcessBuilder.EnableTempDirectory`, pregraph declares `tempDirectory` | Native request supports explicit `temp_directory`; text does not. DirectGraph does not automatically enable temp allocation. Preserve distinct per-pip roots, cleanup/untracking and temp-variable binding, not merely a tracked `TMPDIR` value. |
| Package mutexes and concurrency limits | `bob_plan.execution_plan` records mutexes; traditional `bob_jobq` enforces them; `pregraph.bootstrap` emits mutexes and an optional package semaphore | Native requests support semaphores; text does not. A mutex is a named semaphore with limit/increment 1. The current main Ninja generator does not propagate package mutexes; that pre-existing omission is not fixed by this audit. |
| Acquisition auth/runtime-state untracking | Pregraph declares Azure state scopes and its shared lock file explicitly | Native request supports untracked paths/scopes; text does not. Needed if these acquisition workers are emitted as text, not a reason to migrate the DScript pregraph. Do not broaden all process source permissions. |
| Mutable acquisition cache policy | Pregraph uses both `disableCacheLookup: true` and `uncacheableExitCodes: [0]` for unpinned downloads | Text can express the disable-lookup option, but not uncacheable exit codes; native requests already support both. Disable lookup alone is not the existing full policy. Conditional on acquisition text emission. |
| Literal source-seal exclusions | Main Ninja configuration excludes Git metadata; pregraph source seals exclude selected Jujutsu metadata | Core `PipConstructionHelper.TrySealDirectory` accepts `sourceExclusions`; neither `AddSealDirectoryRequest` nor the text seal operation carries them, and the DirectGraph builder omits them. Requires protocol/native/text plumbing, not only a new text field. |

`package_scratch` deliberately has a sibling-directory fallback when `TMPDIR`
is absent; absence does not itself make the wrapper throw. That fallback is
not equivalent to the BuildXL-managed pip-local scratch used by current
Ninja/DScript execution. Existing tests
`test_native_scratch_uses_tmpdir_and_only_publishes_package_artifacts` and
`test_package_scratch_is_stable_fresh_and_exclusively_owned` document both
behaviors; they were inspected, not rerun for this audit.

Agreed minimum for main-build coarse text emission: explicit and automatic
temp-directory support and semaphores, plus source-seal exclusion plumbing
needed by the already agreed source policy. Expose automatic allocation as an
opt-in native request/text construction setting mapped to
`ProcessBuilder.EnableTempDirectory`, matching DScript's
`tool.prepareTempDirectory`. A uniform boolean in Kati's process additions then
lets the native builder allocate a distinct temp root per process. Preserve
explicit temp-path support and native allocation/untracking/temp-variable
behavior; do not make temp allocation a loader default or a new scheduler
`Process.Options` flag. Caller-supplied explicit paths must also be unique per
process. Include the request change in the planned protocol-version bump.

Add untracked path/scope and uncacheable-exit-code text fields only when
acquisition workers are actually selected for text emission. The current
DScript pregraph already expresses those controls. Source-exclusion protocol
changes must bump `DirectGraphProtocol.Version` and preserve agreed literal
exclusion semantics through the shared construction API.

No use of custom success/retry codes, per-action timeouts, output redirection,
breakaway children or additional temp directories was demonstrated in the
inspected coarse generators. Do not expose the entire native request merely
for hypothetical parity. Installed-file inventory filters are not evidence
that process output-directory exclusions are required.

**Kati plan**

Check real SDK FORCE, recursive Make, grouped-output, implicit-output and
symlink usage against the supported export contract. Add only justified
capabilities or precise rejection diagnostics. Content-addressed reuse of a
FORCE-dependent action is not automatically a bug; track actual inputs rather
than blanket-disabling caching.

**Validation**

Use focused coarse-action fixtures and backend compatibility cases before any
real BoB trial. Cover scratch isolation/cleanup without publishing private
files, conflicting package mutexes, and source-seal exclusions through the full
reader/native construction path. If acquisition text emission is selected,
also cover narrow untracking and both cache-lookup/no-store controls. Preserve
native and Ninja behavior for changed Kati logic.

## Tool changes: measured efficiency and deletions

Keep production stitched Ninja, generic project coordination, actual-artifact
imports, argument factoring, the core source-seal index and external gRPC.

The measurement-first scope is agreed. Do not put speculative parsing/index
rewrites on the integration critical path.
Delete only demonstrably unused code after establishing callers and intended
use. No measurements or deletions are claimed by this design approval.

**Measurement and bounded implementation plan**

- Measure text parsing versus pip construction time/allocations, chunk
  concatenation/escaping/regex costs, repeated normalization and per-reference
  dependency scans.
- Measure source/output overlap scans, equivalent seal duplication, incremental
  directory checks and Kati path/ownership validation.
- Use evidence to select per-project file indexes, containment indexes,
  allocation reductions or empty-index/cache simplifications. Directory
  resolution still needs ancestry, candidate and publisher policy; a global
  export directory is not automatically required.
- Evaluate GraphEmitter example extensions, Ninja project-graph mode,
  renderer/test glue, internal async wrappers, duplicate format documentation
  and `KATI_GROUPED_TARGETS` as conditional deletion candidates. Establish
  callers and intended use before removing public or planned behavior.

**Native construction API extraction: completed by user direction.**
`DirectGraphPipConstructor` owns shared validation and native pip construction
for process, write/copy, seal, composition and export operations. Its operation
types use native paths, strings, actual artifacts and PipData; construction
returns produced artifacts and pip identity directly. Defaults and validation
remain in this one core, not a loader-specific policy engine.

`DirectGraphPipGraphBuilder` is now the protobuf adapter, retaining protocol
version checks, ordered ID registration, operation uniqueness, producer
references, sticky rejection, completion, counters and tracing. It translates
wire arguments/content to native PipData; no PipData field was added to the
protocol and version 4 is unchanged.

`DirectGraphTextLoader` calls the native core directly and no longer constructs
protobuf operations, maintains duplicate ID tables, creates synthetic import
IDs or reads produced artifacts back through the adapter. The mixed PipData
side-channel overload and host import/read-back helpers were removed after
auditing their callers. `DirectGraphValueRegistry` is now owned solely by the
wire example emitter under Examples and no longer ships as an engine type.
Parallel project construction, actual producer/rewrite identities,
native argument factoring/escaping and fingerprint behavior are retained.

Further performance work remains measurement-driven. This layering cleanup
does not claim representative graph-construction speedups or product rollout
validation.

## BoB integration work: separate from tool implementation

These are integration obligations, not policy to hard-code into Kati or tests
claimed to have passed:

- Keep the two-invocation pipeline. Declare pregraph tool/input dependencies
  and tracked evaluation environment; feed generated fragments/configuration
  into the main invocation.
- Reuse `bob_plan.execution_plan` allowlisting and explicit `--plan-env`
  extensions, plus the evaluation/execution split in `bob_ninja.environment`
  and `bob_ninja.execution_environment`. Keep `make.environment` and
  `make.inherit_environment` tracked, registered-tool PATH handling and explicit
  missing-inheritance errors. Do not create another independent allowlist or
  change traditional BoB's default environment behavior.
- Set Kati's pregraph pip environment explicitly using the curated baseline;
  do not inherit the wrapper's entire ambient environment. Exclude credentials
  and session state before launching Kati, not after writing fragments. Check
  evaluation-only metadata handling without silently changing Make behavior.
- Pass the `NJOBS`/`MAKE_PARALL` passthrough-name list to Kati through the
  same emission config. Declare that config file as
  a pregraph pip input. Audit recipe/wrapper uses for late binding; preserve
  tracked values where Make actually uses them during evaluation.
- For coarse workers, keep shell job-control placeholders late-bound, remove
  configured passthrough values from action-side serialized step environments,
  and forward only those named runtime values into child environments. A
  missing runtime value must not restore a captured value. Reuse the wrapper's
  explicit environment-overlay seam where suitable; do not inherit all ambient
  variables or substitute numeric command strings. Verify action definitions
  stay stable across job-control changes and test runtime binding/cache reuse.
  Keep `MAKE_PARALL` consistent with the desired runtime resource policy;
  changing `NJOBS` alone does not recalculate its precomputed flags.
- Switch selected Make projects to Kati DirectGraph action emission. Generate
  coarse fragments and dependency configuration. Place input/source artifact
  construction before actions in `graphFiles`, and output-sealing/composition/
  export operations in trailing policy. Supply required additional inputs,
  writable roots through the single Kati emission config for Make processes
  and explicit fields in coarse fragments; use the agreed passthrough metadata
  contract. Do not encode BoB policy into Kati or assume typed defaults in the
  DirectGraph loader.
- Rely on clean CI state and a fixed graph; no new ownership-region or special
  prebuilt-provider declarations are required. Derive source policies from
  each project's owning repository and selected
  build graph; different repositories/graphs may legitimately use different
  seals. Do not homogenize policies for artifact sharing. Compose exports from
  selected real outputs, not filesystem scans.
- Query Git for submodules recursively during pregraph generation and add their
  Git metadata entries to explicit source-seal exclusions. Handle `.git` as
  either a directory or a gitfile; exclude resolved metadata directories too
  where they fall within authorized source roots. Track the discovery inputs
  needed to regenerate policy when submodule layout changes. Do not rely on
  `.git` matching nested components automatically. Validate with nested
  submodule fixtures before the cleared real trial.
- After tool fixes and retained review, and only with clearance, use isolated
  outputs and a minimum representative closure. Validate actual sandboxed
  execution, repeat-run cache reuse, tracked-environment/fragment invalidation,
  passthrough changes and order-only/probe behavior. Preserve a stitched Ninja
  regression path. Until then, real BoB integration remains unvalidated.

## Execution order and tracked backlog

Finish every open design topic or explicitly defer it, then review the
consolidated design before remaining behavior changes. Read-only inventory and
isolated measurements may inform design. Implement in focused slices, resume
the retained Opus reviewer, address substantive findings, then seek clearance
for BoB integration. Do not substitute parse-only checks for execution.

| Task | Scope / gate |
|---|---|
| `review-approve-design-plan` | Consolidated approval after the remaining topic discussions. |
| `review-implement-process-policy` | Implement Kati's single emission config, prefix IDs and validated process additions; no loader-defaults engine. Design-gated. |
| `review-implement-export-merge` | New configuration/fragment export deduplication across loader/resolver, using actual artifact identity. Design-gated. |
| `review-implement-environment` | Kati/BuildXL tracked and passthrough contract. Design-gated. |
| `review-implement-source-seals` | Only agreed source-policy compatibility changes. Design-gated. |
| `review-implement-output-ownership` | Closed with no change: extra stale-output protection is out of scope. |
| `review-implement-coarse-fields` | Only required coarse-action fields. Design-gated. |
| `review-optimize-seal-sharing` | Deferred by agreement; revisit only for measured construction/graph-size overhead. |
| `review-optimize-journal-index` | Conservative incremental directory indexing after measurement. |
| `review-test-seal-cache-replay` | Exclusion/nested-policy changes, graph reload and cached path sets. |
| `review-validate-source-seal-consumers` | Per-process policy inventory and cleared baseline-versus-strict consumer comparisons; blocks branch rollout, including stitched Ninja. |
| `review-measure-native-overheads` | Representative parsing/construction and exporter costs. |
| `review-extract-native-construction` | Completed: native artifact/PipData core shared by text and protobuf adapters; protocol unchanged. |
| `review-implement-native-optimizations` | Only measured, discussed changes; optional. |
| `review-triage-delta-deletions` | Proven unnecessary delta; conditional removals need approval. |
| `review-inventory-make-contract` | Actual SDK compatibility requirements. |
| `review-opus-final-fixes` | Retained reviewer checks selected fixes/deletions before BoB trial. |
| `review-wait-bob-clearance` | Blocked pending explicit safety clearance. |
| `review-wire-isolated-bob-trial` | Deferred integration with isolated outputs and minimal closure. |
| `review-validate-bob-cache` | Deferred real execution, reuse and invalidation validation. |

The older unidentified Ninja source-seal issue remains independently blocked;
do not equate it with these findings. Use existing targeted tests and formatters,
preserve repository formatting, and avoid unrelated changes or coverage work.

## Completed fixes and review baseline

The original review baseline was BuildXL `949748ddf3` and Kati `4e2e4cd`.
The same Opus 5.5 long-context reviewer is retained for follow-up review.

| Task / commit | Change | Validation |
|---|---|---|
| `review-fix-grouped-rule-selection` / Kati `ca332c4` | Redirect implicit-output members to their owning recipe before implicit/suffix lookup. | Failure reproduced first; both prerequisite orders covered across native, Ninja and BuildXL backends. |
| `review-fix-kati-literal-encoding` / Kati `866e569` | Reject invalid UTF-8 before writing the fragment; escape C1 controls and preserve valid Unicode. | Grouped/exporter suites: 26 top-level tests, 126 test/subtest passes. Existing Ninja implicit/pattern regressions: 20 test/subtest passes. |
| `review-fix-fragment-diagnostics` / BuildXL `1cbb148c22` | Duplicate normalized seal contents report record context; decoding failures identify the fragment. | 75 DirectGraph reader/loader tests passed, including all three new cases. |

Scoped formatting and whitespace checks passed for those fixes. Nothing was
pushed. That evidence does not establish real BoB execution or cache correctness.

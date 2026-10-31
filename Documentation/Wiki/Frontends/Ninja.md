# Building a project from a Ninja specification with BuildXL
BuildXL supports building [Ninja](https://ninja-build.org/) projects with a frontend that knows how to translate the Ninja specification to a BuildXL build graph. 
This can be used to build [CMake projects](cmake-builds-with-ninja.md), but any project that can be built with the Ninja engine is supported. 

## Configuration
Moving an existing repo to BuildXL may need some adjustments to conform to extra build guardrails BuildXL imposes to guarantee safe caching and distribution.
Many repos though may need little or no changes, but that really depends on how well 'disciplined' a repo is.

To specify the build, a `config.dsc` BuildXL configuration file needs to become part of the repo and the Ninja resolver needs to be added to it.
Most of the onboarding work is about adjusting the configuration of this resolver to the particularities of a repository.
The configuration file must indicate the kind of a resolver used ('Ninja') and the location of the repo root. 
Here is an example of such a file:
```typescript
config({
    resolvers: [
    {
        kind: "Ninja",
        moduleName: "my-repo",
        root: d`.`,
        // specFile: f`./build.ninja` [Implicit, see below]
    }
  ]
});
```

Here, the directory pointed by `root` is assumed to have a `build.ninja` file with the specification.
If this is not the case, a `specFile` property (commented out in the example above) is available, where one can point to the `.ninja` file that is the entry point for the specification.

`root` is the working directory for graph construction and build commands.
Paths in the Ninja graph are resolved relative to `root`, even when `specFile`
is stored in a build-output subdirectory.

By default each project root is also an implicit shared opaque output directory.
For builds that keep sources immutable, set `allowWritableSourceDirectory: false`
on the resolver and declare writable roots with `additionalOutputDirectories`
or project `directoryOutputs`. This leaves command working directories unchanged.
Configure source mounts as non-writable and output mounts as writable; explicitly
declared file and directory outputs still undergo normal mount validation.

The full set of options to configure the `Ninja` resolver can be found [here](../../../Public/Sdk/Public/Prelude/Prelude.Configuration.Resolvers.dsc), defined under `interface NinjaResolver`.

On Linux, commands run through `/bin/sh -c`, matching Ninja's shell semantics,
including redirection, pipelines, and quoting around nested shell invocations.
When generating Ninja from Make with Kati, also supply Make's exported variables
through the resolver's `environment` setting. Multi-output generators must declare
all their outputs in the Ninja graph (for example, with Kati's
`.KATI_IMPLICIT_OUTPUTS`); do not give each generated file a separate placeholder
recipe that claims ownership of the same files.

## Building with BuildXL
After [installing BuildXL](../../Wiki/Installation.md), one can run the project pointing to the `config.dsc` configuration file: `bxl /c:config.dsc`. 

## Experimental grouped Ninja graphs

Use `projects` within one Ninja resolver to connect separately generated graphs.
Each project has its own working directory, Ninja entry point, targets and
explicit environment. This does not automatically connect separate resolvers.

```typescript
config({
    resolvers: [{
        kind: "Ninja",
        moduleName: "Workspace",
        projects: [
            {
                name: "consumer",
                root: d`consumer`,
                environment: Map.empty<string, string>()
                    .add("PATH", "/usr/bin:/bin")
                    .add("PACKAGE_MODE", "consumer"),
            },
            {
                name: "producer",
                root: d`producer`,
                specFile: f`producer/build/build.ninja`,
                environment: Map.empty<string, string>()
                    .add("PATH", "/usr/bin:/bin")
                    .add("PACKAGE_MODE", "producer"),
            },
        ],
        additionalOutputDirectories: [p`sdk`],
    }],
});
```

Do not combine `projects` with resolver-level `root`, `specFile`, `targets` or
`environment`. Project names must be unique (case-insensitively). Each project
must specify a root or spec file, and an environment map. Omitting `specFile`
uses `build.ninja` in the project root; omitting `targets` imports its default
targets. Relative paths inside each Ninja graph use that project's root.
Configuration path literals such as `specFile` remain relative to the config.

The project environment is a complete specification, not an overlay on another
project or the host environment. An empty map hides user environment variables;
BuildXL still supplies its normal OS/sandbox environment. Tracked values
participate in pip fingerprints. Existing environment data and
`Unit.unit()` passthrough declarations are supported, but passthrough values do
not invalidate cached results and must not control build outputs. Do not embed
secrets in graphs or configuration files.

Before scheduling, the resolver indexes declared outputs across the imported
graphs, resolves matching file inputs to their producers, and topologically
sorts the combined graph. Project list order is not execution order. Duplicate
producers and cycles fail graph construction rather than permitting SDK
multiwrites. Ordinary source inputs continue to use sandbox-observed reads
for incremental dependency tracking.

Each project's pip identity uses its own name and Ninja entry point relative to
its root, not the first project's entry point. Inserting or reordering unrelated
projects therefore preserves existing pip identities and generated temporary
directory paths. Changes within a project's selected graph can still change its
pip identities.

Projects can also export static output directories and import them by producer
name and root:

```typescript
// Producer project:
directoryExports: [p`sdk/include/example`],
// Consumer project:
directoryImports: Map.empty<string, Path[]>()
    .add("producer", [p`sdk/include/example`]),
```

The resolver derives exact membership from the producer's declared file output
artifacts under that root, without enumerating disk or requiring member lists
in configuration. It creates a partial seal and adds a directory dependency to
each process in the importing project. All member producers precede consumers;
file contents affect consumer cache lookup only when actually read or otherwise
observed. Explicit Ninja file inputs retain their original meaning and are not
removed by directory imports.

Exports must be nonempty, nonoverlapping, and contain only file outputs owned by
the named project. Unknown projects/roots, missing exports, self-imports, and
cycles (including cycles combining file and directory dependencies) fail with
project/root diagnostics. Dynamic shared-opaque contents are not discovered or
silently promoted to static membership. Output paths must already be declared
by real producers; a phony install recipe with hidden file writes is insufficient.
Runtime generated-file access violations retain the importing project's pip
identity and accessed path in standard sandbox diagnostics.

### Dynamic directory outputs

For a coarse project with exactly one non-phony node in the selected graph,
use `directoryOutputs` instead of listing generated filenames:

```typescript
// Producer project (one executable node, plus optional phony aliases):
directoryOutputs: [p`sdk/include/generated`],
// Consumer project:
directoryImports: Map.empty<string, Path[]>()
    .add("producer", [p`sdk/include/generated`]),
```

These are dynamic shared-opaque outputs and are automatically exported. The
producer declares its completion marker or other known outputs in Ninja; its
recipe can generate additional files under the configured directory roots.
Importers receive the actual produced directory artifact, including members
unknown during graph construction, rather than a partial seal. Empty directories
are allowed. Producer ordering and cache content tracking apply to dynamic
members too; an explicit Ninja input under an imported directory is not
misclassified as a source file. Known produced file inputs retain their explicit
artifact dependencies, including when another producer shares the same SDK
parent directory.

Roots within one project's `directoryOutputs` must be valid, distinct, and
nonoverlapping, and must not duplicate its `directoryExports`. Shared roots
across dynamic producers are allowed: each shared-opaque artifact contains only
its own producer's files. This does not authorize two producers to write the
same file; normal sandbox and graph multiwrite checks still apply.

Producers must be present in the selected/default target closures.
The resolver does not infer dependencies from
commands, discover generated-header producers from runtime reads, or translate
package-install scripts into declared outputs. Declare generated files and
their consumers explicitly, including SDK installation/copy edges.

Upstream `ckati` can generate the component graphs without modifications.
For example, run `mkdir -p build && ckati --ninja --ninja_dir=build` from each project's root,
then reference `build/build.ninja` in its configuration. Supply the runtime
variables from each Make invocation in the corresponding project environment:
Kati's generated `env*.sh` files are not imported automatically. Generation-time
Make conditionals and shell expressions still require the correct environment
when invoking Kati and regeneration when those inputs change. Graphs requiring
upstream generated configuration may still need staged generation.

## Examples
In the Examples folder in this repository you can find:
- an example [plain Ninja project](../../../Examples/NinjaHelloWorld/). that you can build following the [instructions](../../../Examples/NinjaHelloWorld/README.md)
- an example [CMake project](../../../Examples/CMakeNinjaHelloWorld) to be build with the Ninja resolver following [these instructions](../../../Examples/CMakeNinjaHelloWorld/README.md).

## Appendix: Unvetted implementation notes

- `NinjaGraphStitcher.TryStitch` validates the combined graph and returns action-level
  dependency order, project ownership, canonical directory imports and static export membership.
- `NinjaResolver` passes these bindings to per-project `NinjaPipConstructor` instances
  sharing produced artifacts. BuildXL adds execution edges from the supplied artifacts;
  producers must be constructed before consumers, even when actions interleave projects.
- Static partial exports, dynamic shared-opaque outputs, source seals and the legacy
  single-project configuration remain supported.

### Forward-looking project/subgraph integration

The additive `IProjectToGraphConstructor<TProject>` contract takes a project,
its direct dependencies' `ProjectGraphOutputs`, and a qualifier. It constructs
an arbitrary pip subgraph and returns public artifacts. `IProjectWithDependencies`
remains unchanged, as do the existing MSBuild/JavaScript process constructors
and custom-scheduling hooks. The shared `ProjectGraphToGraphConstructor` computes
and validates the selected project closure before construction, using the existing
topological sorter. Initial construction is sequential; parallelizing independent
projects is a future optimization, not part of the artifact contract.

`ProjectGraphOutputs.Files` maps paths to actual `FileArtifact` values, including
rewrite counts. `Directories` maps roots to `StaticDirectory` values, preserving
directory identity, seal kind, and known membership. There is deliberately no
single owning process ID. A constructor may explicitly re-export dependency
artifacts, but transitive dependency visibility is not automatic. Unscheduled
projects have no exports; their dependencies are still traversed.

For Ninja, setting `dependencies`, `fileExports`, or a nonempty `generatedInputs`
on any grouped project opts the group into this contract:

```typescript
// Provider: its Ninja graph may contain multiple producing actions.
{
    name: "headers",
    root: d`headers`,
    dependencies: [],
    fileExports: [p`sdk/include/api.h`],
    directoryExports: [p`sdk/include`],
    environment: Map.empty<string, string>().add("PATH", "/usr/bin:/bin"),
},
// Consumer:
{
    name: "application",
    root: d`application`,
    dependencies: ["headers"],
    generatedInputs: [p`sdk/include/api.h`],
    environment: Map.empty<string, string>().add("PATH", "/usr/bin:/bin"),
},
```

In this mode, omitted dependency lists mean no dependencies. Names are matched
case-insensitively. Omitted `fileExports` exports all declared file outputs;
an empty list hides them. Export paths must have producers inside the project.
Directory exports remain explicitly configured. Consumers still declare Ninja
file inputs or `directoryImports`; merely listing a project dependency adds no
blanket execution edges. Imported partial-directory members retain their actual
file artifacts, while runtime-discovered opaque members use directory artifacts.
Missing required generated inputs fail linking even when stale files exist on
disk. Ordinary Ninja inputs remain sources when no generated classification or
known producer applies.

The Ninja adapter currently indexes and validates the whole imported group before
project construction. All configured projects are roots; `targets` still controls
each Ninja graph's extraction closure. The generic constructor supports a selected
project closure, but resolver-level project filtering and lazy loading of unrelated
invalid graphs are not implemented. Action-level cycles remain errors; additionally,
project cycles are rejected even when the action graph itself is acyclic.
Projects whose actions interleave must retain the legacy mode by omitting the new
settings on every project.

The initial adapter remains within one Ninja resolver. Mixed resolver types,
DScript lookup/callback projection, independently cached graph fragments, and
per-project graph patching are future work. Whole-graph reuse continues through
the existing frontend tracking mechanism: unchanged graphs can be reused, while
Ninja metadata changes require reconstruction. Source seals are private project
inputs, not automatically public exports.
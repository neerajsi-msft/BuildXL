It can be painful to have to build a list of source files to create a [Sealed Directory](../Sealed-Directories.md). This can be because the list is large or simply because it changes frequently. BuildXL therefore has a feature called Source Sealed Directories.

For all intents and purposes they behave almost exactly the same as Full Sealed Directories with the caveat that you don't have to list all the files in the directory.

For this to be safe it means that this can only be a source directory. In other words, no pip is allowed to write output files into that directory.

For performance reasons Source Sealed Directories do not enumerate all the files from the file system at graph construction time, it does this as the pips read files from that folder. As a result the `contents` field of the `SealedDirectory` object is empty for these just like for [Opaque Sealed Directories](./Opaque-Sealed-Directories.md).

If you want access to the list of files at graph construction time, you will need to use a regular fully sealed directory and produce the list of files using  [`glob`](../../DScript/Globbing.md) and [`globR`](../../DScript/Globbing.md) .

Users can create two types of Source Sealed Directories, one that only allows the files in the directory by using the `topDirectoryOnly` option. If all files recursively should be part of this Sealed Directory you can use the `allDirectories` option.

```ts
// Source: These seal all the files in the source tree. No pip is allowed to write to that folder. The contents field is empty after creation.
const sourceTop = Transformer.sealSourceDirectory(d`dir1`, SealSourceDirectoryOption.topDirectoryOnly);
const sourceAll = Transformer.sealSourceDirectory(d`dir2`, SealSourceDirectoryOption.allDirectories);
```

### Excluding files and subtrees

The object form accepts `excludedPaths`: nonempty literal paths relative to the
seal root. Each excludes the entry itself and its entire subtree:

```ts
const sources = Transformer.sealSourceDirectory({
    root: d`repo`,
    include: "allDirectories",
    excludedPaths: [r`private/build-state`, r`.git`, r`subrepo/.git`],
});
```

These are component-aware literal matches, not wildcard or Gitignore patterns.
For example, excluding `.git` covers only `repo/.git`, whether a directory or a
pointer file. It does not exclude `repo/subrepo/.git`, `.github` or `.gitignore`.
Nested repository metadata requires an explicit path such as `subrepo/.git`.

Exclusions do not hide filesystem entries. Enumerating an allowed parent still
observes the excluded name, and adding, removing, or renaming that name affects
the enumeration fingerprint. Changing contents below the excluded entry does not
affect a process that only enumerates its parent.

An exact existence/type probe of the excluded entry is allowed; reading it,
enumerating an excluded directory, or probing any descendant is not authorized by
this seal. On Linux eBPF, `TreatStatAsProbe=false` classifies `stat` as a read and
therefore requires read authorization. It does not fingerprint every metadata
field returned by `stat`.

Within a process's source-directory dependencies, the deepest declared root
governs scope and basename patterns. A nonmatching deeper policy does not fall
back to a broader seal. Policies at the same root combine their inclusion
patterns, but exclusions are terminal boundaries: they cannot be bypassed by
another source seal. Declaring a source root at or below any exclusion in that
process's source seals is a graph-construction error, independent of declaration
order.

A separately declared file input, or an explicitly listed member of a regular
sealed directory, can still authorize a file. Exclusions are not global denies,
untracked scopes, or a mechanism for banning a particular executable. Processes
with different source-directory policies are validated independently.

The scheduler indexes roots and exclusions by interned paths and walks an
observation's ancestors to find its policy. Equivalent source-policy sets share
one immutable, validated index for the pip table's lifetime, regardless of seal
artifact IDs or declaration order. Graph construction binds each process to that
index; access processing and cache lookup reuse it. Loading a saved graph rebuilds
these derived indexes on demand, once per distinct policy set.

Each observation-processing pass also keeps one matched boundary node. Sibling
observations reuse their parent directory's node, but still check the observed
entry for a nested root or exclusion. Subtree reuse is allowed only when the
cached node has no child policy boundaries, including exclusions contributed by
broader seals. Patterns, top-only scope and exact-probe flags are reevaluated for
every access. This optimization does not depend on observation ordering and
never stores mutable observation state in the shared index.

Ninja projects can declare equivalent recursive dependencies using
`sourceDirectories`, either on a grouped project or on a single-project resolver:

```ts
sourceDirectories: [{
    root: d`repo`,
    excludedPaths: [r`private/build-state`, r`.git`, r`subrepo/.git`],
}],
```

Every executable node in that project consumes these seals. The roots must be
under read-only mounts, as for DScript source seals. Ordinary Ninja file inputs
remain explicit grants, including any explicitly listed excluded file.

Declaring a nonempty `sourceDirectories` list opts that project into declared-input
checking: it disables Ninja's legacy undeclared source reads and automatic Git
untracking. Projects without source seals retain their previous behavior. Explicit
user-configured untracked scopes remain untracked and should not overlap paths
whose access must be checked.

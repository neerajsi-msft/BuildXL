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

The object form accepts `excludedPaths` (nonempty paths relative to the seal root)
and `excludedNames` (entry names matched at every depth). Both exclude the entry
itself and its entire subtree from the seal's read authorization:

```ts
const sources = Transformer.sealSourceDirectory({
    root: d`repo`,
    include: "allDirectories",
    excludedPaths: [r`private/build-state`],
    excludedNames: [a`.git`],
});
```

These are component-aware literal matches, not wildcard or Gitignore patterns.
For example, excluding `.git` also covers nested repositories and `.git` pointer
files, but does not exclude `.github` or `.gitignore`.

Exclusions do not hide filesystem entries. Enumerating an allowed parent still
observes the excluded name, and adding, removing, or renaming that name affects
the enumeration fingerprint. Changing contents below the excluded entry does not
affect a process that only enumerates its parent.

An exact existence/type probe of the excluded entry is allowed; reading it,
enumerating an excluded directory, or probing any descendant is not authorized by
this seal. On Linux eBPF, `TreatStatAsProbe=false` classifies `stat` as a read and
therefore requires read authorization. It does not fingerprint every metadata
field returned by `stat`.

An exclusion restricts only this seal's grant: a separately declared file input
or another directory input may authorize the same path. It is not a global deny,
an untracked scope, or a mechanism for banning a particular executable.

Ninja projects can declare equivalent recursive dependencies using
`sourceDirectories`, either on a grouped project or on a single-project resolver:

```ts
sourceDirectories: [{
    root: d`repo`,
    excludedPaths: [r`private/build-state`],
    excludedNames: [a`.git`],
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

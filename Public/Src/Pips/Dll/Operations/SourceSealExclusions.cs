// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics.ContractsLight;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// File or subtree exclusions from a source seal's access grant, not from disk enumeration.
    /// </summary>
    public sealed class SourceSealExclusions
    {
        /// <summary>Exclusions relative to the seal root.</summary>
        public ReadOnlyArray<RelativePath> Paths { get; }

        /// <summary>Entry names excluded at every depth below the seal root.</summary>
        public ReadOnlyArray<PathAtom> Names { get; }

        /// <summary>No exclusions.</summary>
        public static readonly SourceSealExclusions Empty = new SourceSealExclusions(
            ReadOnlyArray<RelativePath>.Empty, ReadOnlyArray<PathAtom>.Empty);

        /// <summary>Whether this seal has any exclusions.</summary>
        public bool IsEmpty => Paths.Length == 0 && Names.Length == 0;

        /// <nodoc />
        public SourceSealExclusions(ReadOnlyArray<RelativePath> paths, ReadOnlyArray<PathAtom> names)
        {
            Contract.Requires(paths.IsValid);
            Contract.Requires(names.IsValid);
            Contract.RequiresForAll(paths, path => path.IsValid && !path.IsEmpty);
            Contract.RequiresForAll(names, name => name.IsValid);
            Paths = paths;
            Names = names;
        }

        /// <summary>
        /// Whether a path is excluded. An exact excluded entry can still be probed for existence/type;
        /// its descendants cannot be accessed through this seal.
        /// </summary>
        public bool Contains(PathTable pathTable, AbsolutePath root, AbsolutePath path, bool allowExactProbe = false)
        {
            if (IsEmpty || path == root || !path.IsWithin(pathTable, root))
            {
                return false;
            }

            foreach (var relativePath in Paths)
            {
                var excluded = root.Combine(pathTable, relativePath);
                if (path == excluded ? !allowExactProbe : path.IsWithin(pathTable, excluded))
                {
                    return true;
                }
            }

            for (var current = path; current != root; current = current.GetParent(pathTable))
            {
                foreach (var name in Names)
                {
                    var currentName = current.GetName(pathTable);
                    bool matches = OperatingSystemHelper.IsPathComparisonCaseSensitive
                        ? currentName == name
                        : currentName.CaseInsensitiveEquals(pathTable.StringTable, name);
                    if (matches && (current != path || !allowExactProbe))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <nodoc />
        public void Serialize(BuildXLWriter writer)
        {
            writer.Write(Paths, (w, path) => w.Write(path));
            writer.Write(Names, (w, name) => w.Write(name));
        }

        /// <nodoc />
        public static SourceSealExclusions Deserialize(BuildXLReader reader)
        {
            var paths = reader.ReadReadOnlyArray(r => r.ReadRelativePath());
            var names = reader.ReadReadOnlyArray(r => r.ReadPathAtom());
            return paths.Length == 0 && names.Length == 0 ? Empty : new SourceSealExclusions(paths, names);
        }
    }
}

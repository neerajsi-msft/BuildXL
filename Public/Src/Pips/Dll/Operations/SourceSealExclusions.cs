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

        /// <summary>No exclusions.</summary>
        public static readonly SourceSealExclusions Empty = new SourceSealExclusions(
            ReadOnlyArray<RelativePath>.Empty);

        /// <summary>Whether this seal has any exclusions.</summary>
        public bool IsEmpty => Paths.Length == 0;

        /// <nodoc />
        public SourceSealExclusions(ReadOnlyArray<RelativePath> paths)
        {
            Contract.Requires(paths.IsValid);
            Contract.RequiresForAll(paths, path => path.IsValid && !path.IsEmpty);
            Paths = paths;
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

            return false;
        }

        /// <nodoc />
        public void Serialize(BuildXLWriter writer)
        {
            writer.Write(Paths, (w, path) => w.Write(path));
        }

        /// <nodoc />
        public static SourceSealExclusions Deserialize(BuildXLReader reader)
        {
            var paths = reader.ReadReadOnlyArray(r => r.ReadRelativePath());
            return paths.Length == 0 ? Empty : new SourceSealExclusions(paths);
        }
    }
}

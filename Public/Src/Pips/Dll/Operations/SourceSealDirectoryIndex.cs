// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using BuildXL.Utilities.Core;

namespace BuildXL.Pips.Operations
{
    /// <summary>The applicable source-seal policy for an observation.</summary>
    public enum SourceSealMatch
    {
        /// <summary>No source seal grants access.</summary>
        None,
        /// <summary>The deepest source root grants access.</summary>
        Included,
        /// <summary>The observation is a seal root, whose enumeration must be tracked.</summary>
        Root,
        /// <summary>The observation falls under an exclusion.</summary>
        Excluded,
    }

    /// <summary>
    /// Per-process policy boundaries keyed by interned paths. Lookup walks ancestors once,
    /// stopping at an exclusion or the deepest declared root.
    /// </summary>
    /// <remarks>Graph construction rejects source roots at or below an exclusion.</remarks>
    public sealed class SourceSealDirectoryIndex
    {
        /// <summary>A path-table-independent index for an empty source policy set.</summary>
        public static SourceSealDirectoryIndex Empty { get; } = new SourceSealDirectoryIndex(null, Array.Empty<SourceSealWithPatterns>());

        /// <summary>Whether this index contains no source policies or exclusions.</summary>
        public bool IsEmpty => m_boundaries.Count == 0;

        private readonly Dictionary<AbsolutePath, BoundaryNode> m_boundaries = new Dictionary<AbsolutePath, BoundaryNode>();

        private sealed class BoundaryNode
        {
            public readonly AbsolutePath Path;
            public readonly List<SourceSealWithPatterns> Policies = new List<SourceSealWithPatterns>();
            public BoundaryNode Parent;
            public bool IsExcluded;
            public bool HasChildren;

            public BoundaryNode(AbsolutePath path)
            {
                Path = path;
            }
        }

        /// <summary>The source root that conflicts with an exclusion, if any.</summary>
        public AbsolutePath ConflictingRoot { get; }

        /// <summary>The exclusion containing <see cref="ConflictingRoot"/>, if any.</summary>
        public AbsolutePath ConflictingExclusion { get; }

        /// <summary>Whether no positive root is at or below an exclusion.</summary>
        public bool IsValid => !ConflictingRoot.IsValid;

        /// <summary>Builds and validates an immutable policy index.</summary>
        public SourceSealDirectoryIndex(PathTable pathTable, IEnumerable<SourceSealWithPatterns> seals)
        {
            foreach (var seal in seals)
            {
                GetOrAddBoundary(seal.Path).Policies.Add(seal);
                foreach (var path in seal.Exclusions.Paths)
                {
                    GetOrAddBoundary(seal.Path.Combine(pathTable, path)).IsExcluded = true;
                }
            }

            foreach (var node in m_boundaries.Values)
            {
                node.Parent = FindBoundary(pathTable, node.Path.GetParent(pathTable));
                if (node.Parent != null)
                {
                    node.Parent.HasChildren = true;
                }
            }

            foreach (var node in m_boundaries.Values)
            {
                if (node.Policies.Count != 0)
                {
                    for (var current = node; current != null; current = current.Parent)
                    {
                        if (current.IsExcluded)
                        {
                            ConflictingRoot = node.Path;
                            ConflictingExclusion = current.Path;
                            return;
                        }
                    }
                }
            }
        }

        private BoundaryNode GetOrAddBoundary(AbsolutePath path)
        {
            if (!m_boundaries.TryGetValue(path, out var node))
            {
                node = new BoundaryNode(path);
                m_boundaries.Add(path, node);
            }

            return node;
        }

        /// <summary>Classifies an observation without scanning unrelated seals or exclusion lists.</summary>
        public SourceSealMatch Match(PathTable pathTable, AbsolutePath path, bool allowExactExcludedProbe = false)
        {
            Contract.Requires(IsValid);
            return MatchNode(pathTable, path, FindBoundary(pathTable, path), allowExactExcludedProbe);
        }

        /// <summary>Classifies an observation using a caller-owned, pass-local matched-node cache.</summary>
        public SourceSealMatch Match(PathTable pathTable, AbsolutePath path, ref LookupCache cache, bool allowExactExcludedProbe = false)
        {
            Contract.Requires(IsValid);
            return path.IsValid ? cache.Match(this, pathTable, path, allowExactExcludedProbe) : SourceSealMatch.None;
        }

        private SourceSealMatch MatchNode(PathTable pathTable, AbsolutePath path, BoundaryNode node, bool allowExactExcludedProbe)
        {
            if (node != null && node.IsExcluded && path == node.Path && allowExactExcludedProbe)
            {
                // Skipping an exact boundary probe must not bypass an outer exclusion.
                node = node.Parent;
            }

            if (node != null && node.IsExcluded)
            {
                return SourceSealMatch.Excluded;
            }

            if (node == null)
            {
                return SourceSealMatch.None;
            }

            if (path == node.Path)
            {
                return SourceSealMatch.Root;
            }

            foreach (var policy in node.Policies)
            {
                if ((!policy.IsTopDirectoryOnly || path.GetParent(pathTable) == policy.Path)
                    && policy.MatchesPatterns(pathTable, path))
                {
                    return SourceSealMatch.Included;
                }
            }

            return SourceSealMatch.None;
        }

        /// <summary>Whether a failed access is below a top-only seal with matching patterns.</summary>
        public bool IsUnderTopDirectoryOnly(PathTable pathTable, AbsolutePath path)
        {
            Contract.Requires(IsValid);
            var node = FindBoundary(pathTable, path);
            if (node != null && !node.IsExcluded && path != node.Path)
            {
                foreach (var policy in node.Policies)
                {
                    if (policy.IsTopDirectoryOnly && policy.MatchesPatterns(pathTable, path))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private BoundaryNode FindBoundary(PathTable pathTable, AbsolutePath path)
        {
            if (m_boundaries.Count == 0)
            {
                return null;
            }

            for (var current = path; current.IsValid; current = current.GetParent(pathTable))
            {
                if (m_boundaries.TryGetValue(current, out var node))
                {
                    return node;
                }
            }

            return null;
        }

        /// <summary>
        /// One-element locality cache. The node is immutable; access flags and basename patterns
        /// are evaluated afresh. Separate processing passes must use separate cache instances.
        /// </summary>
        public struct LookupCache
        {
            private SourceSealDirectoryIndex m_index;
            private AbsolutePath m_directory;
            private BoundaryNode m_node;

            /// <summary>Directory resolutions avoided through sibling or boundary-free subtree locality.</summary>
            public long Hits { get; private set; }

            /// <summary>Directory resolutions requiring an ancestor lookup.</summary>
            public long Misses { get; private set; }

            internal SourceSealMatch Match(SourceSealDirectoryIndex index, PathTable pathTable, AbsolutePath path, bool allowExactExcludedProbe)
            {
                var directory = path.GetParent(pathTable);
                if (ReferenceEquals(m_index, index)
                    && (directory == m_directory
                        || (m_node != null && !m_node.HasChildren && directory.IsWithin(pathTable, m_node.Path))))
                {
                    Hits++;
                }
                else
                {
                    m_index = index;
                    m_node = index.FindBoundary(pathTable, directory);
                    Misses++;
                }

                m_directory = directory;

                // A leaf can itself be a nested root or exclusion, even on a sibling-cache hit.
                if (!index.m_boundaries.TryGetValue(path, out var node))
                {
                    node = m_node;
                }

                return index.MatchNode(pathTable, path, node, allowExactExcludedProbe);
            }
        }

        /// <summary>Interns equivalent policy sets for the lifetime of a pip table.</summary>
        public sealed class Cache
        {
            private static readonly ObjectPool<HashSet<SourceSealWithPatterns>> s_policySets = Pools.CreateSetPool<SourceSealWithPatterns>();
            private readonly ConcurrentDictionary<PipId, SourceSealDirectoryIndex> m_pipIndices =
                new ConcurrentDictionary<PipId, SourceSealDirectoryIndex>();

            private readonly ConcurrentDictionary<HashSet<SourceSealWithPatterns>, Lazy<SourceSealDirectoryIndex>> m_indices =
                new ConcurrentDictionary<HashSet<SourceSealWithPatterns>, Lazy<SourceSealDirectoryIndex>>(HashSet<SourceSealWithPatterns>.CreateSetComparer());

            /// <summary>Looks up a nonempty index already bound to this pip, without enumerating policies.</summary>
            public bool TryGet(PipId pipId, out SourceSealDirectoryIndex index) => m_pipIndices.TryGetValue(pipId, out index);

            /// <summary>Number of nonempty per-pip bindings retained by this cache.</summary>
            public int BoundIndexCount => m_pipIndices.Count;

            /// <summary>Returns the shared index, ignoring artifact IDs, duplicates and seal order.</summary>
            public SourceSealDirectoryIndex GetOrAdd(PathTable pathTable, IEnumerable<SourceSealWithPatterns> seals, PipId pipId = default)
            {
                if (pipId.IsValid && m_pipIndices.TryGetValue(pipId, out var existing))
                {
                    return existing;
                }

                using var pooled = s_policySets.GetInstance();
                var probe = pooled.Instance;
                probe.UnionWith(seals);
                if (probe.Count == 0)
                {
                    return Empty;
                }

                var index = m_indices.TryGetValue(probe, out var cached) ? cached.Value : AddPolicySet(pathTable, probe);
                return pipId.IsValid ? m_pipIndices.GetOrAdd(pipId, index) : index;
            }

            private SourceSealDirectoryIndex AddPolicySet(PathTable pathTable, HashSet<SourceSealWithPatterns> probe)
            {
                // Only retained keys own their storage. A hit never captures or retains the pooled probe.
                var key = new HashSet<SourceSealWithPatterns>(probe);
                return m_indices.GetOrAdd(key,
                    policies => new Lazy<SourceSealDirectoryIndex>(() => new SourceSealDirectoryIndex(pathTable, policies))).Value;
            }

            /// <summary>Binds a validated index after graph construction assigns the pip's ID.</summary>
            public void Bind(PipId pipId, SourceSealDirectoryIndex index)
            {
                Contract.Requires(pipId.IsValid);
                Contract.Requires(index.IsValid);
                if (index.IsEmpty)
                {
                    Contract.Assert(!m_pipIndices.ContainsKey(pipId), "A pip's source policy index cannot change.");
                    return;
                }
                var existing = m_pipIndices.GetOrAdd(pipId, index);
                Contract.Assert(ReferenceEquals(existing, index), "A pip's source policy index cannot change.");
            }
        }
    }
}

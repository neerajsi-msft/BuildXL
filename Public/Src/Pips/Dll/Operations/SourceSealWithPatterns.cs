// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Diagnostics.ContractsLight;
using BuildXL.Native.IO;
using BuildXL.Utilities;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;

namespace BuildXL.Pips.Operations
{
    /// <summary>
    /// Source seal directory path with list of wildcard patterns
    /// </summary>
    public readonly struct SourceSealWithPatterns : IEquatable<SourceSealWithPatterns>
    {
        /// <summary>
        /// Source seal directory path
        /// </summary>
        public readonly AbsolutePath Path;

        private readonly int m_hashCode;

        /// <summary>
        /// Wildcard patterns that filter the contents of the source seal directory
        /// </summary>
        public readonly ReadOnlyArray<StringId> Patterns;

        /// <summary>
        /// Whether the source seal directory is top-level directory only or not.
        /// </summary>
        public readonly bool IsTopDirectoryOnly;

        /// <summary>Entries not authorized by this source seal.</summary>
        public readonly SourceSealExclusions Exclusions;

        /// <summary>
        /// Constructor
        /// </summary>
        public SourceSealWithPatterns(AbsolutePath path, ReadOnlyArray<StringId> patterns, bool isTopDirectoryOnly, SourceSealExclusions exclusions = null)
        {
            Contract.Requires(path.IsValid);

            Path = path;
            Patterns = patterns.IsValid ? patterns : ReadOnlyArray<StringId>.Empty;
            IsTopDirectoryOnly = isTopDirectoryOnly;
            Exclusions = exclusions ?? SourceSealExclusions.Empty;
            m_hashCode = ComputeHashCode(Path, Patterns, IsTopDirectoryOnly, Exclusions);
        }

        /// <inheritdoc />
        public bool Equals(SourceSealWithPatterns other)
        {
            if (Path != other.Path) return false;
            if (!Path.IsValid) return true;
            if (m_hashCode != other.m_hashCode) return false;
            if (IsTopDirectoryOnly != other.IsTopDirectoryOnly ||
                Patterns.Length != other.Patterns.Length || Exclusions.Paths.Length != other.Exclusions.Paths.Length)
            {
                return false;
            }
            for (int i = 0; i < Patterns.Length; i++)
            {
                if (Patterns[i] != other.Patterns[i]) return false;
            }
            for (int i = 0; i < Exclusions.Paths.Length; i++)
            {
                if (Exclusions.Paths[i] != other.Exclusions.Paths[i]) return false;
            }
            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is SourceSealWithPatterns other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => m_hashCode;

        private static int ComputeHashCode(AbsolutePath path, ReadOnlyArray<StringId> patterns, bool isTopDirectoryOnly, SourceSealExclusions exclusions)
        {
            int hash = HashCodeHelper.Combine(path.GetHashCode(), isTopDirectoryOnly ? 1 : 0);
            foreach (var pattern in patterns)
            {
                hash = HashCodeHelper.Combine(hash, pattern.GetHashCode());
            }

            foreach (var exclusion in exclusions.Paths)
            {
                hash = HashCodeHelper.Combine(hash, exclusion.GetHashCode());
            }

            return hash;
        }

        /// <summary>
        /// Check whether the source seal directory contains the given child path based on the wildcard patterns
        /// </summary>
        public bool Contains(PathTable pathTable, AbsolutePath childPath, bool? isTopDirectoryOnlyOverride = default, bool allowExactExcludedProbe = false)
        {
#if PLATFORM_WIN
            Contract.Requires(pathTable != null);
            Contract.Requires(childPath.IsValid);                
#else
            if (pathTable == null || !childPath.IsValid)
            {
                return false;
            }
#endif
            bool checkIsTopDirectoryOnly = isTopDirectoryOnlyOverride ?? IsTopDirectoryOnly;
            bool checkPatterns = false;

            if (checkIsTopDirectoryOnly && childPath.GetParent(pathTable) == Path)
            {
                checkPatterns = true;
            }

            if (!checkIsTopDirectoryOnly && childPath != Path && childPath.IsWithin(pathTable, Path))
            {
                checkPatterns = true;
            }

            if (!checkPatterns)
            {
                return false;
            }

            if (Exclusions.Contains(pathTable, Path, childPath, allowExactExcludedProbe))
            {
                return false;
            }

            return MatchesPatterns(pathTable, childPath);
        }

        /// <summary>Matches basename patterns after the caller has resolved scope and exclusions.</summary>
        internal bool MatchesPatterns(PathTable pathTable, AbsolutePath childPath)
        {
            if (!Patterns.IsValid || Patterns.Length == 0)
            {
                return true;
            }

            var childPathName = childPath.GetName(pathTable).ToString(pathTable.StringTable);
            foreach (var pattern in Patterns)
            {
                var patternStr = pattern.ToString(pathTable.StringTable);

                // As this is potentially a hot path in the ObservedInputProcessor, we should replace it 
                // with SingleWildcardPatternMatch in case of a perf regression.
                if (FileUtilities.PathMatchPattern(childPathName, patternStr))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether the filename passes the given wildcard pattern
        /// </summary>
        /// <remarks>
        /// This is intentionally non-standard wildcard pattern matching. 
        /// Only one wildcard char, '*' is allowed and '?' is not supported.
        /// This method is at least 3 times faster than 'PathMatchSpecW' from 'shlwapi.dll'
        /// </remarks>
        public static bool SingleWildcardPatternMatch(string name, string pattern)
        {
            Contract.Requires(name != null);
            Contract.Requires(!string.IsNullOrEmpty(pattern));

            // p is an index for pattern
            // n is an index for name
            int n = 0;
            for (int p = 0; p < pattern.Length; p++)
            {
                char patternChar = pattern[p];

                if (patternChar == '*')
                {
                    int patternRemainderLength = pattern.Length - p - 1;
                    n = name.Length - patternRemainderLength;
                }
                else if (patternChar == '?')
                {
                    Contract.Assert(false, "Question mark is not allowed in source seal wildcard patterns");
                }
                else if (n >= 0 && n < name.Length)
                {
                    if (patternChar.ToUpperInvariantFast() != name[n].ToUpperInvariantFast())
                    {
                        return false;
                    }

                    n++;
                }
                else
                {
                    return false;
                }
            }

            if (name.Length != n)
            {
                return false;
            }

            return true;
        }
    }
}

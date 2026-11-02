// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration
{
    /// <summary>A recursive source directory dependency for a Ninja project.</summary>
    public interface INinjaSourceDirectorySettings
    {
        /// <summary>Source seal root, under a read-only mount.</summary>
        AbsolutePath Root { get; }

        /// <summary>Root-relative excluded files or subtrees.</summary>
        IReadOnlyList<RelativePath> ExcludedPaths { get; }

        /// <summary>Entry names excluded at every depth.</summary>
        IReadOnlyList<PathAtom> ExcludedNames { get; }
    }
}

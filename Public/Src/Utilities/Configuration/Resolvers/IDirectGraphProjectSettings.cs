// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration
{
    /// <summary>
    /// A project constructed from streaming DirectGraph text and optional policy fragments.
    /// </summary>
    public interface IDirectGraphProjectSettings
    {
        /// <summary>Project identity and exported DScript module name.</summary>
        PathAtom Name { get; }

        /// <summary>Base for relative artifact paths.</summary>
        AbsolutePath Root { get; }

        /// <summary>Action fragments, read in order. May be empty for a policy-only provider.</summary>
        IReadOnlyList<AbsolutePath> GraphFiles { get; }

        /// <summary>Policy fragments, read after the action fragments in the same project context.</summary>
        IReadOnlyList<AbsolutePath> PolicyFiles { get; }

        /// <summary>Direct dependencies whose published artifacts this project may consume.</summary>
        IReadOnlyList<string> Dependencies { get; }

        /// <summary>Files to publish after loading all fragments. Omitted means no additional exports.</summary>
        IReadOnlyList<AbsolutePath> FileExports { get; }

        /// <summary>Existing directory artifacts to publish. Ambiguous roots require an explicit policy record.</summary>
        IReadOnlyList<AbsolutePath> DirectoryExports { get; }
    }
}
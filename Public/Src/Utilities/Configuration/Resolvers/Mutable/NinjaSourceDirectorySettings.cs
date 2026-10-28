// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <inheritdoc />
    public sealed class NinjaSourceDirectorySettings : INinjaSourceDirectorySettings
    {
        /// <nodoc />
        public NinjaSourceDirectorySettings() { }

        /// <nodoc />
        public NinjaSourceDirectorySettings(INinjaSourceDirectorySettings template, PathRemapper remapper)
        {
            Root = remapper.Remap(template.Root);
            ExcludedPaths = template.ExcludedPaths?.Select(path => remapper.Remap(path)).ToArray();
            ExcludedNames = template.ExcludedNames?.Select(name => remapper.Remap(name)).ToArray();
        }

        /// <inheritdoc />
        public AbsolutePath Root { get; set; }
        /// <inheritdoc />
        public IReadOnlyList<RelativePath> ExcludedPaths { get; set; }
        /// <inheritdoc />
        public IReadOnlyList<PathAtom> ExcludedNames { get; set; }
    }
}

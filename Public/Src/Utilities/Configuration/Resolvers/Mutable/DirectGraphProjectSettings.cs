// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <inheritdoc />
    public sealed class DirectGraphProjectSettings : IDirectGraphProjectSettings
    {
        /// <nodoc />
        public DirectGraphProjectSettings() { }

        /// <nodoc />
        public DirectGraphProjectSettings(IDirectGraphProjectSettings template, PathRemapper pathRemapper)
        {
            Name = pathRemapper.Remap(template.Name);
            Root = pathRemapper.Remap(template.Root);
            GraphFiles = template.GraphFiles == null ? null : pathRemapper.Remap(template.GraphFiles);
            PolicyFiles = template.PolicyFiles == null ? null : pathRemapper.Remap(template.PolicyFiles);
            Dependencies = template.Dependencies;
            FileExports = template.FileExports == null ? null : pathRemapper.Remap(template.FileExports);
            DirectoryExports = template.DirectoryExports == null ? null : pathRemapper.Remap(template.DirectoryExports);
        }

        /// <inheritdoc />
        public PathAtom Name { get; set; }
        /// <inheritdoc />
        public AbsolutePath Root { get; set; }
        /// <inheritdoc />
        public IReadOnlyList<AbsolutePath> GraphFiles { get; set; }
        /// <inheritdoc />
        public IReadOnlyList<AbsolutePath> PolicyFiles { get; set; }
        /// <inheritdoc />
        public IReadOnlyList<string> Dependencies { get; set; }
        /// <inheritdoc />
        public IReadOnlyList<AbsolutePath> FileExports { get; set; }
        /// <inheritdoc />
        public IReadOnlyList<AbsolutePath> DirectoryExports { get; set; }
    }
}
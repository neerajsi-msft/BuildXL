// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BuildXL.Pips;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver
{
    /// <summary>
    /// Producer-bound exports of a project, potentially originating from multiple pips.
    /// Construction order alone does not introduce execution dependencies.
    /// </summary>
    public sealed class ProjectGraphOutputs
    {
        /// <summary>Exported files indexed by absolute path, preserving rewrite counts.</summary>
        public IReadOnlyDictionary<AbsolutePath, FileArtifact> Files { get; }

        /// <summary>Exported directories, preserving seal kind, identity, and known membership.</summary>
        public IReadOnlyDictionary<AbsolutePath, StaticDirectory> Directories { get; }

        /// <nodoc/>
        public ProjectGraphOutputs(IEnumerable<FileArtifact> files, IEnumerable<StaticDirectory> directories)
        {
            Files = new ReadOnlyDictionary<AbsolutePath, FileArtifact>(files.ToDictionary(file => file.Path));
            Directories = new ReadOnlyDictionary<AbsolutePath, StaticDirectory>(
                directories.ToDictionary(directory => directory.Root.Path));
        }
    }
}

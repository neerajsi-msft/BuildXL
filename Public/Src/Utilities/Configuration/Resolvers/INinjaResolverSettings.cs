// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration
{
    /// <summary>
    /// Settings for Ninja resolver
    /// </summary>
    public interface INinjaResolverSettings : IProjectGraphResolverSettings
    {
        /// <summary>
        /// Optional group of graphs to stitch by declared input/output paths.
        /// Mutually exclusive with resolver-level Root, SpecFile, Targets and Environment.
        /// </summary>
        IReadOnlyList<INinjaProjectSettings> Projects { get; }

        /// <summary>
        /// Targets to resolve
        /// </summary>
        IReadOnlyList<string> Targets { get; }

        /// <summary>
        /// The build file, typically build.ninja. If null, "{ProjectRoot}/build.ninja" is used
        /// </summary>
        AbsolutePath SpecFile { get; }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.Utilities.Configuration.Resolvers;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration
{
    /// <summary>
    /// Execution context for one graph in a grouped Ninja resolver.
    /// </summary>
    public interface INinjaProjectSettings
    {
        /// <summary>Unique project name within the resolver.</summary>
        string Name { get; }

        /// <summary>Working directory and base for relative graph paths.</summary>
        AbsolutePath Root { get; }

        /// <summary>Graph entry point. Defaults to build.ninja under Root.</summary>
        AbsolutePath SpecFile { get; }

        /// <summary>Targets to import, or the graph defaults when omitted.</summary>
        IReadOnlyList<string> Targets { get; }

        /// <summary>Explicit environment for this project, including passthrough declarations.</summary>
        IReadOnlyDictionary<string, EnvironmentData> Environment { get; }

        /// <summary>Partial seals derived from this project's declared file outputs, never disk enumeration.</summary>
        IReadOnlyList<AbsolutePath> DirectoryExports { get; }

        /// <summary>Producer project names and exported directory roots consumed by every process in this project.</summary>
        IReadOnlyDictionary<string, IReadOnlyList<AbsolutePath>> DirectoryImports { get; }
    }
}

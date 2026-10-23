// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.Utilities.Configuration.Resolvers;
using BuildXL.Utilities.Core;

namespace BuildXL.Utilities.Configuration.Mutable
{
    /// <inheritdoc />
    public sealed class NinjaProjectSettings : INinjaProjectSettings
    {
        /// <nodoc />
        public NinjaProjectSettings() { }

        /// <nodoc />
        public NinjaProjectSettings(INinjaProjectSettings template, PathRemapper pathRemapper)
        {
            Name = template.Name;
            Root = pathRemapper.Remap(template.Root);
            SpecFile = pathRemapper.Remap(template.SpecFile);
            Targets = template.Targets;
            Environment = template.Environment;
        }

        /// <inheritdoc />
        public string Name { get; set; }
        /// <inheritdoc />
        public AbsolutePath Root { get; set; }
        /// <inheritdoc />
        public AbsolutePath SpecFile { get; set; }
        /// <inheritdoc />
        public IReadOnlyList<string> Targets { get; set; }
        /// <inheritdoc />
        public IReadOnlyDictionary<string, EnvironmentData> Environment { get; set; }
    }
}

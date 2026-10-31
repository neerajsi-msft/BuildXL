// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.FrontEnd.Sdk.ProjectGraph;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.Ninja
{
    /// <summary>Resolved execution context and imported nodes for one Ninja graph.</summary>
    internal sealed class NinjaProject : IProjectWithDependencies<NinjaProject>
    {
        public string Name { get; }
        public AbsolutePath Root { get; }
        public AbsolutePath SpecFile { get; }
        public IReadOnlyList<string> Targets { get; }
        public IDictionary<string, string> Environment { get; }
        public ICollection<string> PassthroughVariables { get; }
        public IReadOnlyCollection<NinjaNode> Nodes { get; set; }
        public IReadOnlyList<AbsolutePath> DirectoryExports { get; }
        public IReadOnlyList<AbsolutePath> DirectoryOutputs { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<AbsolutePath>> DirectoryImports { get; }
        public IReadOnlyList<INinjaSourceDirectorySettings> SourceDirectories { get; }
        public IReadOnlyList<string> DependencyNames { get; }
        public IReadOnlyList<AbsolutePath> FileExports { get; }
        public IReadOnlyList<AbsolutePath> GeneratedInputs { get; }
        public IReadOnlyCollection<NinjaProject> Dependencies { get; set; } = System.Array.Empty<NinjaProject>();

        public bool CanBeScheduled() => true;

        public override string ToString() => Name;

        public NinjaProject(INinjaProjectSettings settings, PathTable pathTable)
        {
            Name = settings.Name;
            Root = settings.Root.IsValid ? settings.Root : settings.SpecFile.GetParent(pathTable);
            SpecFile = settings.SpecFile.IsValid ? settings.SpecFile : Root.Combine(pathTable, "build.ninja");
            Targets = settings.Targets;
            DependencyNames = settings.Dependencies;
            FileExports = settings.FileExports;
            GeneratedInputs = settings.GeneratedInputs ?? System.Array.Empty<AbsolutePath>();
            SourceDirectories = settings.SourceDirectories ?? System.Array.Empty<INinjaSourceDirectorySettings>();
            DirectoryExports = settings.DirectoryExports ?? System.Array.Empty<AbsolutePath>();
            DirectoryOutputs = settings.DirectoryOutputs ?? System.Array.Empty<AbsolutePath>();
            DirectoryImports = settings.DirectoryImports ?? new Dictionary<string, IReadOnlyList<AbsolutePath>>();
            global::BuildXL.Utilities.Configuration.Mutable.ResolverSettings.ComputeEnvironment(
                settings.Environment, pathTable, out var environment, out var passthrough, out _);
            Environment = environment;
            PassthroughVariables = passthrough;
        }
    }
}

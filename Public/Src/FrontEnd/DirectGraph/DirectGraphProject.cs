// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuildXL.FrontEnd.Sdk.ProjectGraph;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.DirectGraph
{
    /// <summary>A project whose pip subgraph is described by serial text fragments.</summary>
    public sealed class DirectGraphProject : IProjectWithDependencies<DirectGraphProject>
    {
        /// <nodoc />
        public IDirectGraphProjectSettings Settings { get; }

        /// <nodoc />
        public string Name { get; }

        /// <inheritdoc />
        public IReadOnlyCollection<DirectGraphProject> Dependencies { get; private set; }

        private DirectGraphProject(IDirectGraphProjectSettings settings, string name)
        {
            Settings = settings;
            Name = name;
        }

        /// <inheritdoc />
        public bool CanBeScheduled() => true;

        /// <inheritdoc />
        public override string ToString() => Name;

        /// <summary>Validates the whole configured DAG and returns the roots for the shared project linker.</summary>
        public static IReadOnlySet<DirectGraphProject> Create(
            IReadOnlyList<IDirectGraphProjectSettings> settings, IReadOnlyList<string> roots, PathTable pathTable)
        {
            if (settings == null || settings.Count == 0)
            {
                throw new InvalidDataException("At least one text project is required.");
            }

            var projects = new Dictionary<string, DirectGraphProject>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in settings)
            {
                if (item == null || !item.Name.IsValid || !item.Root.IsValid)
                {
                    throw new InvalidDataException("Each text project requires a valid name and root.");
                }
                string name = item.Name.ToString(pathTable.StringTable);
                if (!names.Add(name))
                {
                    throw new InvalidDataException($"Duplicate project name '{name}' (names must also differ ignoring case).");
                }
                if ((item.GraphFiles ?? Array.Empty<AbsolutePath>()).Concat(item.PolicyFiles ?? Array.Empty<AbsolutePath>()).Any(file => !file.IsValid) ||
                    (item.FileExports ?? Array.Empty<AbsolutePath>()).Concat(item.DirectoryExports ?? Array.Empty<AbsolutePath>()).Any(path => !path.IsValid))
                {
                    throw new InvalidDataException($"Project '{name}' contains an invalid fragment or export path.");
                }
                projects.Add(name, new DirectGraphProject(item, name));
            }
            foreach (var project in projects.Values)
            {
                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                var resolved = new List<DirectGraphProject>();
                foreach (string name in project.Settings.Dependencies ?? Array.Empty<string>())
                {
                    if (name == null || !dependencies.Add(name))
                    {
                        throw new InvalidDataException($"Project '{project.Name}' has an empty or repeated dependency.");
                    }
                    if (!projects.TryGetValue(name, out var dependency))
                    {
                        throw new InvalidDataException($"Unknown project '{name}'.");
                    }
                    resolved.Add(dependency);
                }
                project.Dependencies = resolved.AsReadOnly();
            }
            if (!ProjectGraphToPipGraphConstructor<DirectGraphProject>.TryTopoSortProjectsAndComputeClosure(
                projects.Values.ToHashSet(), out _, out var cycle))
            {
                throw new InvalidDataException($"Project dependency cycle: {string.Join(" -> ", cycle.Select(project => project.Name))}.");
            }

            var selected = new HashSet<DirectGraphProject>();
            foreach (string name in roots ?? projects.Keys.ToArray())
            {
                if (name == null || !projects.TryGetValue(name, out var project))
                {
                    throw new InvalidDataException($"Unknown root project '{name}'.");
                }
                selected.Add(project);
            }
            return selected;
        }
    }
}
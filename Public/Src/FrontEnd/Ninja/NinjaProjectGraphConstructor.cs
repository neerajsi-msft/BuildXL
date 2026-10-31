// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.Pips;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.Ninja
{
    /// <summary>Links Ninja subgraphs through the same artifact contract used by other project types.</summary>
    internal sealed class NinjaProjectGraphConstructor : IProjectToGraphConstructor<NinjaProject>
    {
        private readonly NinjaGraphStitcher m_graph;
        private readonly PathTable m_pathTable;
        private readonly Func<NinjaProject, NinjaPipConstructor.SharedState, QualifierId, NinjaPipConstructor> m_createConstructor;
        private readonly IReadOnlyDictionary<AbsolutePath, NinjaProject> m_producers;
        private readonly IReadOnlyDictionary<NinjaProject, Serialization.NinjaNode[]> m_nodes;
        private readonly IReadOnlyCollection<(NinjaProject Project, AbsolutePath Root)> m_dynamicOutputs;

        public NinjaProjectGraphConstructor(
            NinjaGraphStitcher graph,
            PathTable pathTable,
            Func<NinjaProject, NinjaPipConstructor.SharedState, QualifierId, NinjaPipConstructor> createConstructor)
        {
            m_graph = graph;
            m_pathTable = pathTable;
            m_createConstructor = createConstructor;
            m_producers = graph.Nodes.SelectMany(pair => pair.Node.Outputs.Select(path => (path, pair.Project)))
                .ToDictionary(pair => pair.path, pair => pair.Project);
            m_nodes = graph.Nodes.GroupBy(pair => pair.Project)
                .ToDictionary(group => group.Key, group => group.Select(pair => pair.Node).ToArray());
            m_dynamicOutputs = m_nodes.Keys.SelectMany(project => project.DirectoryOutputs.Select(root => (project, root))).ToArray();
        }

        public Possible<ProjectGraphOutputs> TryScheduleProject(
            NinjaProject project,
            IReadOnlyDictionary<NinjaProject, ProjectGraphOutputs> dependencyOutputs,
            QualifierId qualifierId)
        {
            var state = new NinjaPipConstructor.SharedState { DirectoryMembers = m_graph.DirectoryMembers };
            foreach (var dependency in dependencyOutputs)
            {
                foreach (var file in dependency.Value.Files.Values)
                {
                    state.OutputFileArtifacts.Add(file.Path, file);
                }

                state.ScheduledNodes.UnionWith(dependency.Key.Nodes.Where(node => node.Rule != "phony"));
            }

            foreach (var import in m_graph.DirectoryImports[project])
            {
                var dependency = project.Dependencies.SingleOrDefault(candidate => candidate.Name == import.Key);
                if (dependency == null || !dependencyOutputs.TryGetValue(dependency, out var outputs))
                {
                    return new Failure<string>($"Project '{project.Name}' imports directories from '{import.Key}' without a direct project dependency.");
                }

                foreach (var root in import.Value)
                {
                    if (!outputs.Directories.TryGetValue(root, out var directory))
                    {
                        return new Failure<string>($"Project '{project.Name}' imports an unexported directory from '{import.Key}': '{root.ToString(m_pathTable)}'.");
                    }

                    state.SealedDirectories.Add((import.Key, root), directory.Root);
                    foreach (var member in directory.Contents)
                    {
                        state.OutputFileArtifacts.TryAdd(member.Path, member);
                    }
                }
            }

            var nodes = m_nodes.TryGetValue(project, out var projectNodes) ? projectNodes : Array.Empty<Serialization.NinjaNode>();
            bool isImportedDynamicFile(AbsolutePath input) =>
                m_graph.DirectoryImports[project].Any(import =>
                    import.Value.Any(root => !m_graph.DirectoryMembers.ContainsKey((import.Key, root))
                        && input.IsWithin(m_pathTable, root)));

            foreach (var input in project.GeneratedInputs)
            {
                if (!input.IsValid
                    || !(m_producers.TryGetValue(input, out var producer) && producer == project)
                        && !state.OutputFileArtifacts.ContainsKey(input)
                        && (m_producers.ContainsKey(input) || !isImportedDynamicFile(input)))
                {
                    return new Failure<string>($"Project '{project.Name}' has an unresolved generated input '{(input.IsValid ? input.ToString(m_pathTable) : "<invalid>")}'.");
                }
            }

            foreach (var node in nodes)
            {
                foreach (var input in node.Inputs)
                {
                    if (m_producers.TryGetValue(input, out var producer) && producer != project
                        && !state.OutputFileArtifacts.ContainsKey(input))
                    {
                        return new Failure<string>($"Project '{project.Name}' reads generated file '{input.ToString(m_pathTable)}' from '{producer.Name}' without a direct project dependency exporting that file.");
                    }

                    // Runtime-discovered files must be represented by an imported opaque directory,
                    // not silently reclassified as source files, even when a stale file exists on disk.
                    if (!m_producers.ContainsKey(input)
                        && m_dynamicOutputs.Any(output => output.Project != project && input.IsWithin(m_pathTable, output.Root))
                        && !isImportedDynamicFile(input))
                    {
                        return new Failure<string>($"Project '{project.Name}' reads dynamic output '{input.ToString(m_pathTable)}' without a directory import.");
                    }
                }
            }

            var fileExports = project.FileExports ?? nodes.SelectMany(node => node.Outputs).ToArray();
            foreach (var path in fileExports)
            {
                if (!path.IsValid || !m_producers.TryGetValue(path, out var producer) || producer != project)
                {
                    return new Failure<string>($"Project '{project.Name}' exports a file without an internal producer: '{(path.IsValid ? path.ToString(m_pathTable) : "<invalid>")}'.");
                }
            }

            if (fileExports.Distinct().Count() != fileExports.Count)
            {
                return new Failure<string>($"Project '{project.Name}' has duplicate file exports.");
            }

            var constructor = m_createConstructor(project, state, qualifierId);
            foreach (var node in nodes)
            {
                if (!constructor.TrySchedulePip(node, out _))
                {
                    return new Failure<string>($"Failed to schedule Ninja rule '{node.Rule}' in project '{project.Name}'. See the preceding pip diagnostics.");
                }
            }

            var directories = new List<StaticDirectory>();
            foreach (var root in project.DirectoryExports.Concat(project.DirectoryOutputs))
            {
                if (!constructor.TryGetDirectoryExport(root, out var directory))
                {
                    return new Failure<string>($"Failed to seal export '{root.ToString(m_pathTable)}' of project '{project.Name}'.");
                }

                directories.Add(directory);
            }

            return new ProjectGraphOutputs(
                fileExports.Select(path => state.OutputFileArtifacts[path]), directories);
        }
    }
}

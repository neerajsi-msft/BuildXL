// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.Ninja
{
    /// <summary>Validated bindings and dependency order for the union of the imported graphs.</summary>
    internal sealed class NinjaGraphStitcher
    {
        public IReadOnlyList<(NinjaNode Node, NinjaProject Project)> Nodes { get; private init; }
        public IReadOnlyDictionary<(string, AbsolutePath), AbsolutePath[]> DirectoryMembers { get; private init; }
        public IReadOnlyDictionary<NinjaProject, IReadOnlyDictionary<string, IReadOnlyList<AbsolutePath>>> DirectoryImports { get; private init; }

        private NinjaGraphStitcher() { }

        public static bool TryStitch(
            IReadOnlyList<NinjaProject> projects,
            PathTable pathTable,
            out NinjaGraphStitcher graph,
            out string error)
        {
            graph = null;
            error = null;
            var owners = new Dictionary<NinjaNode, NinjaProject>();
            var producers = new Dictionary<AbsolutePath, NinjaNode>();
            var nodes = new List<NinjaNode>();
            foreach (var project in projects)
            {
                foreach (var node in project.Nodes.Where(node => node.Rule != "phony"))
                {
                    owners.Add(node, project);
                    nodes.Add(node);
                    foreach (var output in node.Outputs)
                    {
                        if (producers.TryGetValue(output, out var previous))
                        {
                            error = $"Duplicate Ninja producer for '{output.ToString(pathTable)}': " +
                                $"project '{owners[previous].Name}' rule '{previous.Rule}' and project '{project.Name}' rule '{node.Rule}'.";
                            return false;
                        }

                        producers.Add(output, node);
                    }
                }
            }

            var dependents = nodes.ToDictionary(node => node, _ => new List<NinjaNode>());
            var exports = new Dictionary<(string, AbsolutePath), HashSet<NinjaNode>>();
            foreach (var project in projects)
            {
                if (project.DirectoryOutputs.Count != 0 && project.Nodes.Count(node => node.Rule != "phony") != 1)
                {
                    error = $"Project '{project.Name}' with directoryOutputs must contain exactly one executable node.";
                    return false;
                }

                foreach (var root in project.DirectoryOutputs)
                {
                    if (!root.IsValid || exports.ContainsKey((project.Name, root))
                        || project.DirectoryExports.Contains(root)
                        || project.DirectoryOutputs.Any(other => other != root &&
                            (root.IsWithin(pathTable, other) || other.IsWithin(pathTable, root))))
                    {
                        error = $"Invalid or duplicate directory output in project '{project.Name}'.";
                        return false;
                    }

                    exports.Add((project.Name, root), project.Nodes.Where(node => node.Rule != "phony").ToHashSet());
                }
            }

            var directoryMembers = new Dictionary<(string, AbsolutePath), AbsolutePath[]>();
            foreach (var project in projects)
            {
                foreach (var root in project.DirectoryExports)
                {
                    if (!root.IsValid || exports.ContainsKey((project.Name, root)))
                    {
                        error = $"Invalid or duplicate directory export in project '{project.Name}'.";
                        return false;
                    }

                    foreach (var other in exports.Keys)
                    {
                        if (root == other.Item2 || root.IsWithin(pathTable, other.Item2) || other.Item2.IsWithin(pathTable, root))
                        {
                            error = $"Overlapping Ninja directory exports: project '{other.Item1}' root '{other.Item2.ToString(pathTable)}' and project '{project.Name}' root '{root.ToString(pathTable)}'.";
                            return false;
                        }
                    }

                    var members = producers.Where(pair => pair.Key.IsWithin(pathTable, root)).ToList();
                    if (members.Count == 0 || members.Any(pair => owners[pair.Value] != project))
                    {
                        error = $"Directory export '{root.ToString(pathTable)}' in project '{project.Name}' is empty or contains outputs owned by another project. Only declared, single-owner file outputs can be partially sealed.";
                        return false;
                    }

                    exports.Add((project.Name, root), members.Select(pair => pair.Value).ToHashSet());
                    directoryMembers.Add((project.Name, root), members.Select(pair => pair.Key).ToArray());
                }
            }

            var directoryDependencies = new Dictionary<NinjaProject, HashSet<NinjaNode>>();
            var directoryImports = new Dictionary<NinjaProject, IReadOnlyDictionary<string, IReadOnlyList<AbsolutePath>>>();
            var projectsByName = projects.ToDictionary(project => project.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects)
            {
                var required = new HashSet<NinjaNode>();
                var imports = new Dictionary<string, IReadOnlyList<AbsolutePath>>();
                if (project.DirectoryImports.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != project.DirectoryImports.Count)
                {
                    error = $"Project '{project.Name}' has duplicate directory-import producer names.";
                    return false;
                }

                foreach (var import in project.DirectoryImports)
                {
                    if (import.Value == null || import.Value.Count == 0 || import.Value.Distinct().Count() != import.Value.Count)
                    {
                        error = $"Project '{project.Name}' has an empty or duplicate directory import from '{import.Key}'.";
                        return false;
                    }

                    bool producerFound = projectsByName.TryGetValue(import.Key, out var producerProject);
                    foreach (var root in import.Value)
                    {
                        if (!root.IsValid || !producerFound
                            || producerProject == project || !exports.TryGetValue((producerProject.Name, root), out var members))
                        {
                            error = $"Project '{project.Name}' imports missing, unknown, or self-owned directory '{(root.IsValid ? root.ToString(pathTable) : "<invalid>")}' from project '{import.Key}'.";
                            return false;
                        }

                        required.UnionWith(members);
                    }

                    imports.Add(producerProject.Name, import.Value);
                }

                directoryDependencies.Add(project, required);
                directoryImports.Add(project, imports);
            }

            var indegrees = new Dictionary<NinjaNode, int>();
            foreach (var node in nodes)
            {
                var dependencies = new HashSet<NinjaNode>(node.Dependencies.Where(owners.ContainsKey));
                dependencies.UnionWith(directoryDependencies[owners[node]]);
                foreach (var input in node.Inputs)
                {
                    if (producers.TryGetValue(input, out var producer))
                    {
                        dependencies.Add(producer);
                    }
                }

                indegrees.Add(node, dependencies.Count);
                foreach (var dependency in dependencies)
                {
                    dependents[dependency].Add(node);
                }
            }

            var ready = new Queue<NinjaNode>(nodes.Where(node => indegrees[node] == 0));
            var result = new List<NinjaNode>(nodes.Count);
            while (ready.Count != 0)
            {
                var node = ready.Dequeue();
                result.Add(node);
                foreach (var dependent in dependents[node])
                {
                    if (--indegrees[dependent] == 0)
                    {
                        ready.Enqueue(dependent);
                    }
                }
            }

            if (result.Count != nodes.Count)
            {
                error = "Cycle in stitched Ninja graphs. Blocked nodes: " + string.Join("; ",
                    nodes.Where(node => indegrees[node] != 0).Take(10)
                        .Select(node => $"project '{owners[node].Name}' rule '{node.Rule}' outputs [{string.Join(", ", node.Outputs.Select(path => path.ToString(pathTable)))}]"));
                return false;
            }

            graph = new NinjaGraphStitcher
            {
                Nodes = result.Select(node => (node, owners[node])).ToArray(),
                DirectoryMembers = directoryMembers,
                DirectoryImports = directoryImports,
            };
            return true;
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.Ninja
{
    /// <summary>Validates output ownership and sorts the union of the imported graphs.</summary>
    internal static class NinjaGraphStitcher
    {
        public static bool TrySort(
            IReadOnlyList<NinjaProject> projects,
            PathTable pathTable,
            out IReadOnlyList<NinjaNode> sortedNodes,
            out string error)
        {
            sortedNodes = null;
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
            var indegrees = new Dictionary<NinjaNode, int>();
            foreach (var node in nodes)
            {
                var dependencies = new HashSet<NinjaNode>(node.Dependencies.Where(owners.ContainsKey));
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

            sortedNodes = result;
            return true;
        }
    }
}

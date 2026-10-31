// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.ContractsLight;
using BuildXL.FrontEnd.MsBuild;
using BuildXL.FrontEnd.Sdk.ProjectGraph;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver
{
    /// <summary>
    /// Links the dependency closure of selected projects without requiring one process per project.
    /// Existing process-based constructors and custom scheduling contracts remain unchanged.
    /// </summary>
    public sealed class ProjectGraphToGraphConstructor<TProject> where TProject : IProjectWithDependencies<TProject>
    {
        private readonly IProjectToGraphConstructor<TProject> m_constructor;

        /// <nodoc/>
        public ProjectGraphToGraphConstructor(IProjectToGraphConstructor<TProject> constructor)
        {
            Contract.Requires(constructor != null);
            m_constructor = constructor;
        }

        /// <summary>
        /// Validates the selected closure before constructing any pips, then supplies producer-bound
        /// exports to each dependent. No graph edges are added except those declared by the constructor.
        /// </summary>
        public Possible<IReadOnlyDictionary<TProject, ProjectGraphOutputs>> TrySchedule(
            IReadOnlySet<TProject> projectsToEvaluate,
            QualifierId qualifierId)
        {
            Contract.Requires(qualifierId.IsValid);
            if (!ProjectGraphToPipGraphConstructor<TProject>.TryTopoSortProjectsAndComputeClosure(
                projectsToEvaluate, out var queue, out var cycle))
            {
                return new CycleInProjectsFailure<TProject>(cycle);
            }

            var outputs = new Dictionary<TProject, ProjectGraphOutputs>();
            while (queue.Count != 0)
            {
                var (project, _) = queue.Top;
                queue.Pop();
                if (!project.CanBeScheduled())
                {
                    continue;
                }

                var dependencies = new Dictionary<TProject, ProjectGraphOutputs>();
                foreach (var dependency in project.Dependencies)
                {
                    if (outputs.TryGetValue(dependency, out var dependencyOutputs))
                    {
                        dependencies[dependency] = dependencyOutputs;
                    }
                }

                var result = m_constructor.TryScheduleProject(
                    project, new ReadOnlyDictionary<TProject, ProjectGraphOutputs>(dependencies), qualifierId);
                if (!result.Succeeded)
                {
                    return result.Failure;
                }

                outputs.Add(project, result.Result);
            }

            return new Possible<IReadOnlyDictionary<TProject, ProjectGraphOutputs>>(
                new ReadOnlyDictionary<TProject, ProjectGraphOutputs>(outputs));
        }
    }
}

// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly int m_maxConcurrency;

        /// <nodoc/>
        public ProjectGraphToGraphConstructor(IProjectToGraphConstructor<TProject> constructor)
            : this(constructor, maxConcurrency: 1)
        {
        }

        /// <nodoc/>
        public ProjectGraphToGraphConstructor(IProjectToGraphConstructor<TProject> constructor, int maxConcurrency)
        {
            Contract.Requires(constructor != null);
            Contract.Requires(maxConcurrency > 0);
            m_constructor = constructor;
            m_maxConcurrency = maxConcurrency;
        }

        /// <summary>
        /// Validates the selected closure before constructing any pips, then supplies producer-bound
        /// exports to each dependent. No graph edges are added except those declared by the constructor.
        /// </summary>
        public Possible<IReadOnlyDictionary<TProject, ProjectGraphOutputs>> TrySchedule(
            IReadOnlySet<TProject> projectsToEvaluate,
            QualifierId qualifierId)
            => TryScheduleAsync(projectsToEvaluate, qualifierId).GetAwaiter().GetResult();

        /// <summary>
        /// Runs bounded project construction once all direct dependencies have finished publishing exports.
        /// A failed project prevents its dependents from starting; in-flight independent projects may finish.
        /// </summary>
        public async Task<Possible<IReadOnlyDictionary<TProject, ProjectGraphOutputs>>> TryScheduleAsync(
            IReadOnlySet<TProject> projectsToEvaluate,
            QualifierId qualifierId,
            CancellationToken cancellationToken = default)
        {
            Contract.Requires(qualifierId.IsValid);
            if (!ProjectGraphToPipGraphConstructor<TProject>.TryTopoSortProjectsAndComputeClosure(
                projectsToEvaluate, out var queue, out var cycle))
            {
                return new CycleInProjectsFailure<TProject>(cycle);
            }

            var outputs = new ConcurrentDictionary<TProject, ProjectGraphOutputs>();
            var tasks = new Dictionary<TProject, Task>();
            using var slots = new SemaphoreSlim(m_maxConcurrency);
            Failure failure = null;

            void StopAfterException() =>
                Interlocked.CompareExchange(ref failure, new Failure<string>("Project construction aborted after an exception."), null);

            async Task ConstructAsync(TProject project, Task[] dependencies)
            {
                try
                {
                    await Task.WhenAll(dependencies).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref failure) != null || !project.CanBeScheduled())
                    {
                        return;
                    }
                    await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref failure) != null)
                        {
                            return;
                        }
                        await Task.Run(() =>
                        {
                            try
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (Volatile.Read(ref failure) != null)
                                {
                                    return;
                                }
                                var visible = new Dictionary<TProject, ProjectGraphOutputs>();
                                foreach (var dependency in project.Dependencies)
                                {
                                    if (outputs.TryGetValue(dependency, out var dependencyOutputs))
                                    {
                                        visible[dependency] = dependencyOutputs;
                                    }
                                }
                                var result = m_constructor.TryScheduleProject(
                                    project, new ReadOnlyDictionary<TProject, ProjectGraphOutputs>(visible), qualifierId);
                                if (!result.Succeeded)
                                {
                                    Interlocked.CompareExchange(ref failure, result.Failure, null);
                                }
                                else
                                {
                                    outputs.TryAdd(project, result.Result);
                                }
                            }
                            catch
                            {
                                // Stop admission on this worker before another worker can acquire a released slot.
                                StopAfterException();
                                throw;
                            }
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }
                catch
                {
                    StopAfterException();
                    throw;
                }
            }

            while (queue.Count != 0)
            {
                var (project, _) = queue.Top;
                queue.Pop();
                // Snapshot on this thread: tasks is never read concurrently with its mutation.
                tasks.Add(project, ConstructAsync(project, project.Dependencies.Select(dependency => tasks[dependency]).ToArray()));
            }
            await Task.WhenAll(tasks.Values).ConfigureAwait(false);
            if (failure != null)
            {
                return failure;
            }
            return new Possible<IReadOnlyDictionary<TProject, ProjectGraphOutputs>>(
                new ReadOnlyDictionary<TProject, ProjectGraphOutputs>(new Dictionary<TProject, ProjectGraphOutputs>(outputs)));
        }
    }
}

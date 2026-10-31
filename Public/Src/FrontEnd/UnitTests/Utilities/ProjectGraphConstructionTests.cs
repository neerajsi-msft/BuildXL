// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using BuildXL.FrontEnd.Sdk.ProjectGraph;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.Pips;
using BuildXL.Utilities.Core;
using Xunit;

namespace Test.BuildXL.FrontEnd.Utilities
{
    public sealed class ProjectGraphConstructionTests
    {
        private sealed class Project : IProjectWithDependencies<Project>
        {
            public IReadOnlyCollection<Project> Dependencies { get; set; } = Array.Empty<Project>();
            public bool Scheduled { get; set; } = true;
            public bool CanBeScheduled() => Scheduled;
        }

        private sealed class Constructor : IProjectToGraphConstructor<Project>
        {
            public Func<Project, IReadOnlyDictionary<Project, ProjectGraphOutputs>, Possible<ProjectGraphOutputs>> Schedule { get; set; }

            public Possible<ProjectGraphOutputs> TryScheduleProject(
                Project project, IReadOnlyDictionary<Project, ProjectGraphOutputs> dependencyOutputs, QualifierId qualifierId) =>
                Schedule(project, dependencyOutputs);
        }

        private static ProjectGraphOutputs EmptyOutputs() =>
            new ProjectGraphOutputs(Array.Empty<FileArtifact>(), Array.Empty<StaticDirectory>());

        private static QualifierId EmptyQualifier() => BuildXLContext.CreateInstanceForTesting().QualifierTable.EmptyQualifierId;

        [Fact]
        public void ConstructsOnlySelectedClosureAndExposesOnlyDirectDependencyExports()
        {
            var leaf = new Project();
            var middle = new Project { Dependencies = new[] { leaf } };
            var root = new Project { Dependencies = new[] { middle } };
            var unrelatedCycle = new Project();
            unrelatedCycle.Dependencies = new[] { unrelatedCycle };
            var completed = new Dictionary<Project, ProjectGraphOutputs>();
            var constructor = new Constructor
            {
                Schedule = (project, dependencies) =>
                {
                    Assert.All(project.Dependencies, dependency => Assert.Same(completed[dependency], dependencies[dependency]));
                    Assert.Equal(project.Dependencies.Count, dependencies.Count);
                    var outputs = EmptyOutputs();
                    completed.Add(project, outputs);
                    return outputs;
                },
            };

            var result = new ProjectGraphToGraphConstructor<Project>(constructor).TrySchedule(new HashSet<Project> { root }, EmptyQualifier());

            Assert.True(result.Succeeded);
            Assert.Equal(3, result.Result.Count);
            Assert.DoesNotContain(unrelatedCycle, completed.Keys);
        }

        [Fact]
        public void RejectsCyclesBeforeConstructingAnyProjects()
        {
            var first = new Project();
            var second = new Project { Dependencies = new[] { first } };
            first.Dependencies = new[] { second };
            var constructor = new Constructor
            {
                Schedule = (_, _) => throw new InvalidOperationException("Cycle validation must precede construction."),
            };

            var result = new ProjectGraphToGraphConstructor<Project>(constructor)
                .TrySchedule(new HashSet<Project> { first }, EmptyQualifier());

            Assert.False(result.Succeeded);
            Assert.Contains("Cycle", result.Failure.Describe());
        }

        [Fact]
        public void StopsBeforeConsumersWhenDependencyConstructionFails()
        {
            var dependency = new Project();
            var consumer = new Project { Dependencies = new[] { dependency } };
            int calls = 0;
            var failure = new Failure<string>("Dependency failed.");
            var constructor = new Constructor
            {
                Schedule = (project, _) =>
                {
                    Assert.Same(dependency, project);
                    calls++;
                    return failure;
                },
            };

            var result = new ProjectGraphToGraphConstructor<Project>(constructor)
                .TrySchedule(new HashSet<Project> { consumer }, EmptyQualifier());

            Assert.False(result.Succeeded);
            Assert.Same(failure, result.Failure);
            Assert.Equal(1, calls);
        }

        [Fact]
        public void TraversesDependenciesOfUnscheduledProjectsWithoutExportingThem()
        {
            var leaf = new Project();
            var skipped = new Project { Scheduled = false, Dependencies = new[] { leaf } };
            var root = new Project { Dependencies = new[] { skipped } };
            var visited = new List<Project>();
            var constructor = new Constructor
            {
                Schedule = (project, dependencies) =>
                {
                    Assert.Empty(dependencies);
                    visited.Add(project);
                    return EmptyOutputs();
                },
            };

            var result = new ProjectGraphToGraphConstructor<Project>(constructor)
                .TrySchedule(new HashSet<Project> { root }, EmptyQualifier());

            Assert.True(result.Succeeded);
            Assert.Equal(new[] { leaf, root }, visited);
        }
    }
}

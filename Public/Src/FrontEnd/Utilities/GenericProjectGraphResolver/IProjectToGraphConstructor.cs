// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using BuildXL.FrontEnd.Sdk.ProjectGraph;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver
{
    /// <summary>Constructs an arbitrary pip subgraph and exports its public artifacts.</summary>
    public interface IProjectToGraphConstructor<TProject> where TProject : IProjectWithDependencies<TProject>
    {
        /// <summary>
        /// Dependencies have already been constructed. Only direct dependencies are visible;
        /// forwarding an artifact requires an explicit re-export. Failed construction aborts linking.
        /// Unscheduled dependencies are absent from <paramref name="dependencyOutputs"/>.
        /// </summary>
        Possible<ProjectGraphOutputs> TryScheduleProject(
            TProject project,
            IReadOnlyDictionary<TProject, ProjectGraphOutputs> dependencyOutputs,
            QualifierId qualifierId);
    }
}

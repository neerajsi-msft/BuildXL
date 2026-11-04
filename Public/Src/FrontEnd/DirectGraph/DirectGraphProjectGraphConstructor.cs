// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;

namespace BuildXL.FrontEnd.DirectGraph
{
    /// <summary>Constructs one DirectGraph project using the shared linker's completed dependency exports.</summary>
    public sealed class DirectGraphProjectGraphConstructor : IProjectToGraphConstructor<DirectGraphProject>
    {
        private readonly DirectGraphTextLoader.SharedState m_state;
        private readonly Func<DirectGraphTextLoader.SharedState, DirectGraphTextLoader> m_createLoader;
        private readonly Func<AbsolutePath, TextReader> m_open;
        private readonly Func<AbsolutePath, bool> m_sourceExists;
        private readonly CancellationToken m_cancellationToken;

        /// <nodoc />
        public DirectGraphProjectGraphConstructor(
            PathTable pathTable,
            IReadOnlyList<IDirectGraphProjectSettings> settings,
            Func<DirectGraphTextLoader.SharedState, DirectGraphTextLoader> createLoader,
            Func<AbsolutePath, TextReader> open,
            Func<AbsolutePath, bool> sourceExists,
            CancellationToken cancellationToken)
        {
            m_state = new DirectGraphTextLoader.SharedState(pathTable, settings);
            m_createLoader = createLoader;
            m_open = open;
            m_sourceExists = sourceExists;
            m_cancellationToken = cancellationToken;
        }

        /// <inheritdoc />
        public Possible<ProjectGraphOutputs> TryScheduleProject(
            DirectGraphProject project,
            IReadOnlyDictionary<DirectGraphProject, ProjectGraphOutputs> dependencyOutputs,
            QualifierId qualifierId)
        {
            m_cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The native builder completes each operation synchronously. The shared construction contract
                // is synchronous too; no transport or emitter task is involved in this path.
                return m_createLoader(m_state).LoadProjectAsync(
                    project.Settings, dependencyOutputs, m_open, m_sourceExists, m_cancellationToken).GetAwaiter().GetResult();
            }
            catch (InvalidDataException exception)
            {
                return new Failure<string>(exception.Message);
            }
        }
    }
}
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Artifacts;
using BuildXL.Storage.ChangeTracking;
using BuildXL.Storage.InputChange;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;

namespace BuildXL.Scheduler.ChangeAffectedOutput
{
    /// <summary>
    /// Class representing source change affected output or inputs.
    /// </summary>
    public sealed class SourceChangeAffectedInputs
    {
        private PathTable PathTable => m_fileContentManager.Host.Context.PathTable;

        private readonly FileContentManager m_fileContentManager;

        /// <summary>
        /// Files which affected by the source change. 
        /// </summary>
        /// <remarks>
        /// This list should be initialized by the input change list provided from the InputChanges configuration option.
        /// On the orchestrator, this list contains all the affected files of the build. It is maintained by calling the function ReportSourceChangeAffectedOutputs() after execution of each pip.
        /// On the workers, this list contains all the affected files distributed to the worker. It is maintained by calling the function ReportSourceChangedAffectedFile() when reporting a file input from orchestrator by worker.  
        /// </remarks>
        private readonly ConcurrentBigSet<AbsolutePath> m_sourceChangeAffectedFiles = new ConcurrentBigSet<AbsolutePath>();

        /// <summary>
        /// Initial source changes.
        /// </summary>
        /// <remarks>
        /// We keep initial source changes for computing changes that reside in source seal directories.
        /// </remarks>
        private readonly HashSet<AbsolutePath> m_initialSourceChanges = new HashSet<AbsolutePath>();

        /// <summary>
        /// Affected implicit members are cached by complete per-pip policy, not by an individual seal.
        /// </summary>
        private readonly ConcurrentDictionary<SourceSealDirectoryIndex, List<AbsolutePath>> m_sourcePolicyAffectedMembers = new();
        private readonly ConcurrentDictionary<DirectoryArtifact, List<AbsolutePath>> m_sourceSealDirectoryAffectedMembers = new();
        private readonly SourceSealDirectoryIndex.Cache m_sourceSealDirectoryIndices;

        /// <nodoc />
        public SourceChangeAffectedInputs(FileContentManager fileContentManager)
            : this(fileContentManager, null)
        {
        }

        /// <nodoc />
        public SourceChangeAffectedInputs(FileContentManager fileContentManager, SourceSealDirectoryIndex.Cache sourceSealDirectoryIndices)
        {
            m_fileContentManager = fileContentManager;
            m_sourceSealDirectoryIndices = sourceSealDirectoryIndices ?? new SourceSealDirectoryIndex.Cache();
        }

        /// <summary>
        /// Initial affected output list with the external source change list
        /// </summary>
        public void InitialAffectedOutputList(InputChangeList inputChangeList, PathTable pathTable)
        {
            if (inputChangeList == null)
            {
                return;
            }

            foreach (var changePath in inputChangeList.ChangedPaths)
            {
                switch (changePath.PathChanges)
                {
                    case PathChanges.DataOrMetadataChanged:
                    case PathChanges.NewlyPresentAsFile:
                        m_sourceChangeAffectedFiles.GetOrAdd(new FileArtifact(AbsolutePath.Create(pathTable, changePath.Path)));
                        break;
                }
            }

            m_initialSourceChanges.AddRange(m_sourceChangeAffectedFiles.UnsafeGetList());
        }

        /// <summary>
        /// Compute the intersection of the pip's dependencies and the affected contents of the build
        /// </summary>
        public IReadOnlyList<AbsolutePath> GetChangeAffectedInputs(Process process)
        {
            var changeAffectedInputs = new HashSet<AbsolutePath>(process.Dependencies.Select(f => f.Path).Where(p => m_sourceChangeAffectedFiles.Contains(p)));
            bool allowUndeclaredSourceReads = process.ProcessAllowsUndeclaredSourceReads;

            foreach (var directory in process.DirectoryDependencies)
            {
                foreach (var file in m_fileContentManager.ListSealedDirectoryContents(directory))
                {
                    if (m_sourceChangeAffectedFiles.Contains(file))
                    {
                        changeAffectedInputs.Add(file.Path);
                    }
                }

                if (allowUndeclaredSourceReads && m_fileContentManager.Host.TryGetSourceSealDirectory(directory, out var sourceSeal))
                {
                    // Preserve the legacy additive seal union; undeclared dynamic reads are reported separately.
                    var affectedMembers = m_sourceSealDirectoryAffectedMembers.GetOrAdd(directory,
                        _ => new List<AbsolutePath>(m_initialSourceChanges.Where(path => sourceSeal.Contains(PathTable, path))));
                    changeAffectedInputs.AddRange(affectedMembers);
                }
            }

            if (!allowUndeclaredSourceReads)
            {
                var index = m_sourceSealDirectoryIndices.TryGet(process.PipId, out var bound) ? bound :
                    m_sourceSealDirectoryIndices.GetOrAdd(PathTable, GetSourcePolicies(process), process.PipId);
                if (!index.IsValid)
                {
                    changeAffectedInputs.AddRange(m_initialSourceChanges);
                }
                else if (!index.IsEmpty)
                {
                    // Access flags are unavailable here, so keep possible exact excluded-boundary probes.
                    var affectedMembers = m_sourcePolicyAffectedMembers.GetOrAdd(index,
                        policy => new List<AbsolutePath>(m_initialSourceChanges.Where(path =>
                            policy.Match(PathTable, path, allowExactExcludedProbe: true) is SourceSealMatch.Included or SourceSealMatch.Root)));
                    changeAffectedInputs.AddRange(affectedMembers);
                }
            }
            return ReadOnlyArray<AbsolutePath>.FromWithoutCopy(changeAffectedInputs.ToArray());
        }

        private IEnumerable<SourceSealWithPatterns> GetSourcePolicies(Process process)
        {
            foreach (var directory in process.DirectoryDependencies)
            {
                if (m_fileContentManager.Host.TryGetSourceSealDirectory(directory, out var policy))
                {
                    yield return policy;
                }
            }
        }

        /// <summary>
        /// Check if the outputs of this pip are affected by the source change
        /// </summary>
        private bool IsOutputAffectedBySourceChange(
            Pip pip,
            ReadOnlyArray<AbsolutePath> dynamicallyObservedFiles)
        {
            switch (pip.PipType)
            {
                case PipType.Process:
                    return IsOutputOfProcessAffectedBySourceChange((Process)pip, dynamicallyObservedFiles);
                case PipType.CopyFile:
                    return m_sourceChangeAffectedFiles.Contains(((CopyFile)pip).Source);
                default:
                    return false;
            }
        }

        private bool IsOutputOfProcessAffectedBySourceChange(
            Process process,
            ReadOnlyArray<AbsolutePath> dynamicallyObservedFiles)
        {
            // sourceChangeAffectedOutputFiles is initilized with the source change list.
            // If it is null, that means no file change for this build.
            // Thus no output is affected by the change.
            if (m_sourceChangeAffectedFiles.Count == 0)
            {
                return false;
            }

            return dynamicallyObservedFiles.Concat(process.Dependencies.Select(f=>f.Path)).Any(f => m_sourceChangeAffectedFiles.Contains(f));
        }

        /// <summary>
        /// This function executes on the orchestrator to maintain the globle source change affected contents
        /// </summary>
        public void ReportSourceChangeAffectedFiles(
            Pip pip,
            ReadOnlyArray<AbsolutePath> dynamicallyObservedFiles,
            ReadOnlyArray<FileArtifact> outputContents)
        {
            if (IsOutputAffectedBySourceChange(pip, dynamicallyObservedFiles))
            {
                foreach (var o in outputContents)
                {
                    m_sourceChangeAffectedFiles.Add(o);
                }
            }
        }

        /// <summary>
        /// Check if this file is a change affected file
        /// </summary>
        public bool IsSourceChangedAffectedFile(AbsolutePath path)
        {
            return m_sourceChangeAffectedFiles.Contains(path);
        }
        /// <summary>
        /// This function execute on worker
        /// </summary>
        public void ReportSourceChangedAffectedFile(AbsolutePath path)
        {
            m_sourceChangeAffectedFiles.Add(path);
        }
    }
}

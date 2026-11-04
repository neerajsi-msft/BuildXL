// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Instrumentation.Common;
using NativeSealKind = BuildXL.Pips.Operations.SealDirectoryKind;

namespace BuildXL.Engine.DirectGraph
{
    /// <summary>
    /// Shared native construction and validation. Operations contain actual artifacts and PipData, never protocol IDs.
    /// Instances contain no stream state; concurrent projects use their own ordered module/symbol helpers.
    /// </summary>
    public sealed class DirectGraphPipConstructor
    {
        private readonly PathTable m_paths;
        private readonly IMutablePipGraph m_graph;
        private readonly ISandboxConfiguration m_sandbox;
        private readonly Func<ProcessBuilder> m_createProcess;
        private readonly PipConstructionHelperResolver m_getHelper;
        private readonly TryRegisterExport m_export;
        private readonly CounterCollection<DirectGraphCounter> m_counters;

        public DirectGraphPipConstructor(PathTable pathTable, IMutablePipGraph graph,
            ISandboxConfiguration sandbox, Func<ProcessBuilder> createProcess,
            PipConstructionHelperResolver getHelper, TryRegisterExport registerExport,
            CounterCollection<DirectGraphCounter> counters = null)
        {
            m_paths = pathTable;
            m_graph = graph;
            m_sandbox = sandbox;
            m_createProcess = createProcess;
            m_getHelper = getHelper;
            m_export = registerExport;
            m_counters = counters ?? new CounterCollection<DirectGraphCounter>();
        }

        public DirectGraphConstructionResult Construct(DirectGraphOperation operation) => Construct(operation, null, countOperation: true);

        // The wire adapter resolves provenance before decoding fields, preserving its validation/error ordering.
        internal PipConstructionHelper ResolveHelper(string module, string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
            {
                throw new DirectGraphInvalidRequestException("The operation does not specify a symbol. Every pip has to name the target it belongs to, so that its provenance is stable across builds.");
            }
            if (!m_getHelper(module, symbol, out var helper, out string error))
            {
                throw new DirectGraphInvalidRequestException(error);
            }
            return helper;
        }

        internal DirectGraphConstructionResult Construct(DirectGraphOperation operation, PipConstructionHelper helper, bool countOperation)
        {
            var (count, duration) = operation switch
            {
                DirectGraphProcess => (DirectGraphCounter.AddProcessCount, DirectGraphCounter.AddProcessDuration),
                DirectGraphWriteFile => (DirectGraphCounter.AddWriteFileCount, DirectGraphCounter.AddWriteFileDuration),
                DirectGraphCopyFile => (DirectGraphCounter.AddCopyFileCount, DirectGraphCounter.AddCopyFileDuration),
                DirectGraphSealDirectory => (DirectGraphCounter.AddSealDirectoryCount, DirectGraphCounter.AddSealDirectoryDuration),
                DirectGraphCompositeDirectory => (DirectGraphCounter.AddCompositeSharedOpaqueDirectoryCount, DirectGraphCounter.AddCompositeSharedOpaqueDirectoryDuration),
                DirectGraphExport => (DirectGraphCounter.AddExportCount, DirectGraphCounter.AddExportDuration),
                _ => throw new DirectGraphInvalidRequestException("The graph operation is not specified."),
            };
            if (countOperation)
            {
                m_counters.IncrementCounter(count);
                using (m_counters.StartStopwatch(duration))
                {
                    return ConstructCore(operation, helper);
                }
            }
            return ConstructCore(operation, helper);
        }

        private DirectGraphConstructionResult ConstructCore(DirectGraphOperation operation, PipConstructionHelper helper)
        {
            try
            {
                if (operation is DirectGraphExport export)
                {
                    return AddExport(export);
                }
                helper ??= ResolveHelper(operation.Module, operation.Symbol);
                return operation switch
                {
                    DirectGraphProcess process => AddProcess(process, helper),
                    DirectGraphWriteFile write => AddWriteFile(write, helper),
                    DirectGraphCopyFile copy => AddCopyFile(copy, helper),
                    DirectGraphSealDirectory seal => AddSealDirectory(seal, helper),
                    DirectGraphCompositeDirectory compose => AddComposite(compose, helper),
                    _ => throw new DirectGraphInvalidRequestException("The graph operation is not specified."),
                };
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or DirectGraphInvalidRequestException or DirectGraphConstructionException))
            {
                throw new DirectGraphConstructionException($"BuildXL could not construct the pip: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private DirectGraphConstructionResult AddProcess(DirectGraphProcess request, PipConstructionHelper helper)
        {
            if (!request.Executable.IsValid)
            {
                throw new DirectGraphInvalidRequestException("The process operation does not specify an executable.");
            }
            RequirePath(request.WorkingDirectory, "working_directory");
            using var builder = m_createProcess();
            builder.Executable = request.Executable;
            builder.WorkingDirectory = DirectoryArtifact.CreateWithZeroPartialSealId(request.WorkingDirectory);
            builder.AddInputFile(request.Executable);
            builder.ToolDescription = request.ToolDescription.IsValid ? request.ToolDescription : StringId.Create(m_paths.StringTable, "DirectGraph process");
            string description = Text(request.Description);
            if (!string.IsNullOrEmpty(description))
            {
                builder.Usage = PipDataBuilder.CreatePipData(m_paths.StringTable, string.Empty, PipDataFragmentEscaping.NoEscaping, description);
            }
            builder.Options = request.Options;
            if (request.Options.HasFlag(Process.Options.AllowPreserveOutputs) && m_sandbox.PreserveOutputsForIncrementalTool)
            {
                builder.Options |= Process.Options.IncrementalTool;
            }
            builder.RewritePolicy = RewritePolicyFor(request);
            if (request.AbsentPathProbeMode.HasValue)
            {
                if (!Enum.IsDefined(request.AbsentPathProbeMode.Value))
                {
                    throw new DirectGraphInvalidRequestException("Invalid absent-path-probe mode.");
                }
                builder.AbsentPathProbeUnderOpaquesMode = request.AbsentPathProbeMode.Value;
            }
            if (request.Arguments.IsValid)
            {
                foreach (var fragment in request.Arguments)
                {
                    builder.ArgumentsBuilder.Add(fragment);
                }
            }
            foreach (var file in request.InputFiles)
            {
                if (!file.IsValid) throw new DirectGraphInvalidRequestException("A file reference in 'input_files' is not specified.");
                builder.AddInputFile(file);
            }
            foreach (var directory in request.InputDirectories)
            {
                if (!directory.IsValid) throw new DirectGraphInvalidRequestException("A directory reference in 'input_directories' is not specified.");
                builder.AddInputDirectory(directory);
            }
            foreach (var output in request.OutputFiles)
            {
                if (!Enum.IsDefined(output.Existence)) throw new DirectGraphInvalidRequestException("Invalid output file existence.");
                if (output.Rewrites.IsValid)
                {
                    if (output.Path.IsValid || output.Rewrites.IsSourceFile)
                    {
                        throw new DirectGraphInvalidRequestException("A rewrite must name a produced file, without another output path.");
                    }
                    builder.AddInputFile(output.Rewrites);
                    builder.AddOutputFile(output.Rewrites, output.Existence);
                }
                else
                {
                    if (!output.Path.IsValid) throw new DirectGraphInvalidRequestException("'output_files' has an entry that sets neither 'path' nor 'rewrites'.");
                    builder.AddOutputFile(output.Path, output.Existence);
                }
            }
            foreach (var output in request.OutputDirectories)
            {
                RequirePath(output.Path, "output_directories");
                if (output.Kind is not (NativeSealKind.SharedOpaque or NativeSealKind.Opaque))
                {
                    throw new DirectGraphInvalidRequestException("An output directory must be shared or exclusive opaque.");
                }
                builder.AddOutputDirectory(output.Kind == NativeSealKind.SharedOpaque
                    ? m_graph.ReserveSharedOpaqueDirectory(output.Path)
                    : DirectoryArtifact.CreateWithZeroPartialSealId(output.Path), output.Kind);
            }
            foreach (var path in request.OutputDirectoryExclusions) builder.AddOutputDirectoryExclusion(path);
            foreach (var path in request.UntrackedPaths) builder.AddUntrackedFile(path);
            foreach (var path in request.UntrackedScopes) builder.AddUntrackedDirectoryScope(DirectoryArtifact.CreateWithZeroPartialSealId(path));
            foreach (var variable in request.EnvironmentVariables)
            {
                RequireString(variable.Name, "environment_variables.name");
                if (variable.IsPassThrough)
                {
                    builder.SetPassthroughEnvironmentVariable(variable.Name);
                }
                else
                {
                    RequireString(variable.Value, "environment_variables.value");
                    builder.SetEnvironmentVariable(variable.Name, PipDataAtom.FromStringId(variable.Value), isPassThrough: false);
                }
            }
            var semaphoreNames = new HashSet<StringId>();
            foreach (var semaphore in request.Semaphores)
            {
                RequireString(semaphore.Name, "semaphores.name");
                if (string.IsNullOrEmpty(Text(semaphore.Name)) || !semaphoreNames.Add(semaphore.Name) ||
                    semaphore.Limit <= 0 || semaphore.IncrementBy <= 0 || semaphore.IncrementBy > semaphore.Limit)
                {
                    throw new DirectGraphInvalidRequestException("Semaphores require unique nonempty names and positive increments no greater than their positive limits.");
                }
                builder.SetSemaphore(Text(semaphore.Name), semaphore.Limit, semaphore.IncrementBy);
            }
            SetRuntime(request, builder);
            if (request.StandardOutput.IsValid) builder.SetStandardOutputFile(request.StandardOutput);
            if (request.StandardError.IsValid) builder.SetStandardErrorFile(request.StandardError);
            if (request.TraceFile.IsValid) builder.SetTraceFile(request.TraceFile);
            if (request.EnableTempDirectory && request.TempDirectory.IsValid)
            {
                throw new DirectGraphInvalidRequestException("enable_temp_directory and temp_directory are mutually exclusive.");
            }
            if (request.EnableTempDirectory) builder.EnableTempDirectory();
            else if (request.TempDirectory.IsValid) builder.SetTempDirectory(DirectoryArtifact.CreateWithZeroPartialSealId(request.TempDirectory));
            builder.AdditionalTempDirectories = ReadOnlyArray<AbsolutePath>.From(request.AdditionalTempDirectories);
            foreach (var input in request.OrderOnlyInputs)
            {
                if (!input.File.IsValid || input.File.IsSourceFile)
                {
                    throw new DirectGraphInvalidRequestException("An order-only prerequisite must name a produced file.");
                }
                if (!input.Producer.IsValid)
                {
                    throw new DirectGraphInvalidRequestException("An order-only prerequisite must name an executable or file-producing operation.");
                }
                builder.AddOrderDependency(input.Producer);
            }
            if (!helper.TryAddProcess(builder, out var outputs, out var process))
            {
                throw new DirectGraphConstructionException("BuildXL rejected the process pip.");
            }
            var files = outputs.GetOutputFiles().ToArray();
            Array.Sort(files, OrdinalPathOnlyFileArtifactComparer.Instance);
            return new(files, outputs.GetOutputDirectories().Select(directory => directory.Root).ToArray(), process.PipId, process.SemiStableHash);
        }

        private void SetRuntime(DirectGraphProcess request, ProcessBuilder builder)
        {
            builder.Tags = ReadOnlyArray<StringId>.From(request.Tags);
            builder.SucceedFastExitCodes = ReadOnlyArray<int>.From(request.SucceedFastExitCodes);
            builder.UncacheableExitCodes = ReadOnlyArray<int>.From(request.UncacheableExitCodes);
            builder.SuccessExitCodes = ReadOnlyArray<int>.From(request.SuccessExitCodes.Concat(request.SucceedFastExitCodes).Concat(request.UncacheableExitCodes).ToArray());
            builder.RetryExitCodes = ReadOnlyArray<int>.From(request.RetryExitCodes);
            builder.EnableMultiLineErrorScanning = request.EnableMultiLineErrorScanning;
            if (request.Timeout > TimeSpan.Zero) builder.Timeout = request.Timeout;
            if (request.WarningTimeout > TimeSpan.Zero) builder.WarningTimeout = request.WarningTimeout;
            if (request.Weight > 0) builder.Weight = request.Weight;
            if (request.Priority > 0) builder.Priority = request.Priority;
            if (request.ProcessRetries > 0) builder.SetProcessRetries(request.ProcessRetries);
            if (request.WarningRegex.IsValid) builder.WarningRegex = new BuildXL.Pips.Operations.RegexDescriptor(request.WarningRegex, RegexOptions.None);
            if (request.ErrorRegex.IsValid) builder.ErrorRegex = new BuildXL.Pips.Operations.RegexDescriptor(request.ErrorRegex, RegexOptions.None);
            builder.AllowedUndeclareSourceReadScopes = ReadOnlyArray<AbsolutePath>.From(request.AllowedUndeclaredSourceReadScopes);
            builder.AllowedUndeclareSourceReadPaths = ReadOnlyArray<AbsolutePath>.From(request.AllowedUndeclaredSourceReadPaths);
            builder.AllowedSurvivingChildProcessNames = ReadOnlyArray<PathAtom>.From(request.AllowedSurvivingChildProcessNames
                .Select(name => ProcessName(name, "allowed_surviving_child_process_names")).ToArray());
            builder.ChildProcessesToBreakawayFromSandbox = ReadOnlyArray<IBreakawayChildProcess>.From(request.BreakawayChildProcesses.Select(child =>
                (IBreakawayChildProcess)new global::BuildXL.Utilities.Configuration.Mutable.BreakawayChildProcess
                {
                    ProcessName = ProcessName(child.ProcessName, "breakaway_child_processes"),
                    RequiredArguments = child.RequiredArguments ?? string.Empty,
                    RequiredArgumentsIgnoreCase = child.RequiredArgumentsIgnoreCase,
                }).ToArray());
            if (request.RetryAttemptEnvironmentVariable.IsValid) builder.SetRetryAttemptEnvironmentVariable(request.RetryAttemptEnvironmentVariable);
        }

        private PathAtom ProcessName(StringId name, string field)
        {
            string value = Text(name);
            if (!name.IsValid || !PathAtom.TryCreate(m_paths.StringTable, value, out var result))
            {
                throw new DirectGraphInvalidRequestException($"'{value}' is not a valid process name for '{field}'.");
            }
            return result;
        }

        private DirectGraphConstructionResult AddWriteFile(DirectGraphWriteFile request, PipConstructionHelper helper)
        {
            RequirePath(request.Destination, "destination");
            if (!request.Content.IsValid) throw new DirectGraphInvalidRequestException("'content' is required.");
            if (!helper.TryWriteFile(request.Destination, request.Content, WriteFileEncoding.Utf8, Strings(request.Tags),
                Text(request.Description), out var artifact, out var pip))
            {
                throw new DirectGraphConstructionException("BuildXL rejected the write-file pip.");
            }
            return new(new[] { artifact }, Array.Empty<DirectoryArtifact>(), pip.PipId, pip.SemiStableHash);
        }

        private DirectGraphConstructionResult AddCopyFile(DirectGraphCopyFile request, PipConstructionHelper helper)
        {
            if (!request.Source.IsValid) throw new DirectGraphInvalidRequestException("The copy-file operation does not specify a source.");
            RequirePath(request.Destination, "destination");
            if (!helper.TryCopyFile(request.Source, request.Destination,
                request.OutputsMustRemainWritable ? CopyFile.Options.OutputsMustRemainWritable : CopyFile.Options.None,
                Strings(request.Tags), Text(request.Description), out var artifact, out var pip))
            {
                throw new DirectGraphConstructionException("BuildXL rejected the copy-file pip.");
            }
            return new(new[] { artifact }, Array.Empty<DirectoryArtifact>(), pip.PipId, pip.SemiStableHash);
        }

        private DirectGraphConstructionResult AddSealDirectory(DirectGraphSealDirectory request, PipConstructionHelper helper)
        {
            RequirePath(request.DirectoryRoot, "directory_root");
            if (request.Kind is not (NativeSealKind.Partial or NativeSealKind.Full or NativeSealKind.SourceAllDirectories or NativeSealKind.SourceTopDirectoryOnly))
            {
                throw new DirectGraphInvalidRequestException("Invalid seal directory kind.");
            }
            if (request.SourceExclusions.Count > 0 && !request.Kind.IsSourceSeal())
            {
                throw new DirectGraphInvalidRequestException("source_exclusions are only allowed for source seals.");
            }
            var exclusions = request.SourceExclusions.Select(id =>
            {
                string value = Text(id);
                if (!RelativePath.TryCreate(m_paths.StringTable, value, out var path) || path.IsEmpty)
                {
                    throw new DirectGraphInvalidRequestException($"Invalid nonempty root-relative source exclusion '{value}'.");
                }
                return path;
            }).ToArray();
            if (!helper.TrySealDirectory(request.DirectoryRoot,
                SortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer>.SortUnsafe(request.Contents.ToArray(), OrdinalFileArtifactComparer.Instance),
                SortedReadOnlyArray<DirectoryArtifact, OrdinalDirectoryArtifactComparer>.SortUnsafe(request.OutputDirectoryContents.ToArray(), OrdinalDirectoryArtifactComparer.Instance),
                request.Kind, Strings(request.Tags), Text(request.Description), Strings(request.Patterns),
                out var artifact, out var pip, request.Scrub, new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(exclusions))))
            {
                throw new DirectGraphConstructionException("BuildXL rejected the seal-directory pip.");
            }
            return new(Array.Empty<FileArtifact>(), new[] { artifact }, pip.PipId, pip.SemiStableHash);
        }

        private DirectGraphConstructionResult AddComposite(DirectGraphCompositeDirectory request, PipConstructionHelper helper)
        {
            RequirePath(request.DirectoryRoot, "directory_root");
            ValidateCompositeDirectories(request.Contents);
            if (request.ContentFilter is { } filter)
            {
                ValidateContentFilterRegex(filter.Regex);
            }
            if (request.Action is not (SealDirectoryCompositionActionKind.WidenDirectoryCone or SealDirectoryCompositionActionKind.NarrowDirectoryCone))
            {
                throw new DirectGraphInvalidRequestException("Invalid composition action.");
            }
            if (!helper.TryComposeSharedOpaqueDirectory(request.DirectoryRoot, request.Contents.ToArray(), request.Action, request.ContentFilter,
                Text(request.Description), Strings(request.Tags), out var artifact, out var pip))
            {
                throw new DirectGraphConstructionException("BuildXL rejected the composed shared opaque directory.");
            }
            return new(Array.Empty<FileArtifact>(), new[] { artifact }, pip?.PipId ?? default, pip?.SemiStableHash ?? 0);
        }

        internal void ValidateCompositeDirectories(IEnumerable<DirectoryArtifact> directories)
        {
            foreach (var directory in directories)
            {
                if (!directory.IsSharedOpaque)
                    throw new DirectGraphInvalidRequestException($"'{directory.Path.ToString(m_paths)}' cannot be composed because it is not a shared opaque directory.");
            }
        }

        internal static void ValidateContentFilterRegex(string regex)
        {
            try { _ = new Regex(regex); }
            catch (ArgumentException exception)
            {
                throw new DirectGraphInvalidRequestException($"'content_filter.regex' is not a valid regular expression: {exception.Message}");
            }
        }

        private DirectGraphConstructionResult AddExport(DirectGraphExport request)
        {
            if (request.Files.Count + request.Directories.Count == 0)
            {
                throw new DirectGraphInvalidRequestException("The export operation does not specify any files or directories.");
            }
            foreach (var artifact in request.Files.Select(FileOrDirectoryArtifact.Create).Concat(request.Directories.Select(FileOrDirectoryArtifact.Create)))
            {
                if (!m_export(request.Module, artifact, out string error)) throw new DirectGraphInvalidRequestException(error);
            }
            return new(Array.Empty<FileArtifact>(), Array.Empty<DirectoryArtifact>());
        }

        private RewritePolicy RewritePolicyFor(DirectGraphProcess request)
        {
            if (request.DoubleWritePolicy.HasValue && request.DoubleWritePolicy.Value is not
                (RewritePolicy.DoubleWritesAreErrors or RewritePolicy.AllowSameContentDoubleWrites or RewritePolicy.UnsafeFirstDoubleWriteWins))
                throw new DirectGraphInvalidRequestException("Invalid double-write policy.");
            if (request.SourceRewritePolicy.HasValue && request.SourceRewritePolicy.Value is not
                (RewritePolicy.SourceRewritesAreErrors or RewritePolicy.SafeSourceRewritesAreAllowed))
                throw new DirectGraphInvalidRequestException("Invalid source-rewrite policy.");
            return (request.DoubleWritePolicy ?? m_sandbox.UnsafeSandboxConfiguration.DoubleWritePolicy()) |
                (request.SourceRewritePolicy ?? m_sandbox.UnsafeSandboxConfiguration.SourceWritePolicy());
        }

        public static void SetProcessOption(DirectGraphProcess process, string name)
        {
            if (name == "unsafeSkipGlobalDependencies")
            {
                process.Options &= ~Process.Options.RequireGlobalDependencies;
                return;
            }
            if (!s_options.TryGetValue(name, out var option)) throw new DirectGraphInvalidRequestException($"Unknown process option '{name}'.");
            process.Options |= option;
        }

        private static readonly IReadOnlyDictionary<string, Process.Options> s_options = new Dictionary<string, Process.Options>(StringComparer.Ordinal)
        {
            ["keepOutputsWritable"] = Process.Options.OutputsMustRemainWritable,
            ["requiresAdmin"] = Process.Options.RequiresAdmin,
            ["isLight"] = Process.Options.IsLight,
            ["scanForBuildxlHints"] = Process.Options.EnableBuildXLHintScanning,
            ["allowUndeclaredSourceReads"] = Process.Options.AllowUndeclaredSourceReads,
            ["preservePathSetCasing"] = Process.Options.PreservePathSetCasing,
            ["enforceWeakFingerprintAugmentation"] = Process.Options.EnforceWeakFingerprintAugmentation,
            ["disableCacheLookup"] = Process.Options.DisableCacheLookup,
            ["uncancellable"] = Process.Options.Uncancellable,
            ["mustRunOnOrchestrator"] = Process.Options.MustRunOnOrchestrator,
            ["writingToStandardErrorFailsExecution"] = Process.Options.WritingToStandardErrorFailsExecution,
            ["dependsOnCurrentOs"] = Process.Options.DependsOnCurrentOs,
            ["dependsOnCurrentHostOsDirectories"] = Process.Options.DependsOnCurrentOs,
            ["dependsOnWindowsAppData"] = Process.Options.DependsOnWindowsAppData,
            ["unsafeHasUntrackedChildProcesses"] = Process.Options.HasUntrackedChildProcesses,
            ["unsafeAllowPreservedOutputs"] = Process.Options.AllowPreserveOutputs,
            ["unsafeIncrementalTool"] = Process.Options.IncrementalTool,
            ["unsafeTrustStaticallyDeclaredAccesses"] = Process.Options.TrustStaticallyDeclaredAccesses,
            ["unsafeDisableFullReparsePointResolving"] = Process.Options.DisableFullReparsePointResolving,
            ["unsafeDisableSandboxing"] = Process.Options.DisableSandboxing,
        };

        private string Text(StringId value) => value.IsValid ? value.ToString(m_paths.StringTable) : null;
        private string[] Strings(List<StringId> values) => values.Count == 0 ? null : values.Select(Text).ToArray();
        private static void RequirePath(AbsolutePath path, string field)
        {
            if (!path.IsValid) throw new DirectGraphInvalidRequestException($"'{field}' is required.");
        }
        private static void RequireString(StringId value, string field)
        {
            if (!value.IsValid) throw new DirectGraphInvalidRequestException($"'{field}' is required.");
        }
    }
}

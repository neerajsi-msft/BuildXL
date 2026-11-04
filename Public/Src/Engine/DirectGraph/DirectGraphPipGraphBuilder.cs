// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Engine.DirectGraph.Tracing;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Graph;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Instrumentation.Common;
using InvalidRequestException = BuildXL.Engine.DirectGraph.DirectGraphInvalidRequestException;
using NativeSealKind = BuildXL.Pips.Operations.SealDirectoryKind;

namespace BuildXL.Engine.DirectGraph
{
    public delegate bool PipConstructionHelperResolver(string moduleName, string symbol, out PipConstructionHelper helper, out string error);
    public delegate bool TryRegisterExport(string moduleName, FileOrDirectoryArtifact artifact, out string error);

    /// <summary>
    /// Ordered protobuf stream adapter. Protocol IDs and sticky rejection belong here, not in native construction.
    /// </summary>
    public sealed class DirectGraphPipGraphBuilder : IDirectGraphGraphBuilder
    {
        private readonly PathTable m_pathTable;
        private readonly LoggingContext m_loggingContext;
        private readonly bool m_traceOperation;
        private readonly CounterCollection<DirectGraphCounter> m_counters;
        private readonly List<AbsolutePath> m_paths = new();
        private readonly List<StringId> m_strings = new();
        private readonly Dictionary<ulong, DirectGraphConstructionResult> m_producedArtifacts = new();
        private (ulong OperationId, string Error)? m_firstRejection;
        private bool m_complete;
        public DirectGraphPipConstructor PipConstructor { get; }

        public DirectGraphPipGraphBuilder(PathTable pathTable, IMutablePipGraph pipGraph,
            ISandboxConfiguration sandboxConfiguration, Func<ProcessBuilder> processBuilderFactory,
            PipConstructionHelperResolver pipConstructionHelperResolver, TryRegisterExport registerExport,
            LoggingContext loggingContext, bool traceOperation, CounterCollection<DirectGraphCounter> counters = null)
        {
            Contract.Requires(pathTable != null && pipGraph != null && sandboxConfiguration != null);
            Contract.Requires(processBuilderFactory != null && pipConstructionHelperResolver != null && registerExport != null);
            Contract.Requires(loggingContext != null);
            m_pathTable = pathTable;
            m_loggingContext = loggingContext;
            m_traceOperation = traceOperation;
            m_counters = counters ?? new CounterCollection<DirectGraphCounter>();
            PipConstructor = new(pathTable, pipGraph, sandboxConfiguration, processBuilderFactory,
                pipConstructionHelperResolver, registerExport, m_counters);
        }

        public bool IsComplete => Volatile.Read(ref m_complete);

        public bool TryGetFirstRejection(out ulong operationId, out string error)
        {
            if (m_firstRejection == null) { operationId = 0; error = null; return false; }
            (operationId, error) = m_firstRejection.Value;
            return true;
        }

        public void RegisterImportedArtifacts(ulong importId, FileArtifact[] files, DirectoryArtifact[] directories, PipId producer = default)
        {
            Contract.Requires(importId != 0 && files != null && directories != null);
            Contract.Requires(files.All(file => file.IsValid) && directories.All(directory => directory.IsValid));
            Contract.Requires(!m_complete && m_firstRejection == null);
            files = files.ToArray();
            Array.Sort(files, OrdinalPathOnlyFileArtifactComparer.Instance);
            m_producedArtifacts.Add(importId, new(files, directories.ToArray(), producer));
        }

        public FileArtifact GetProducedFile(ulong operationId, AbsolutePath path, out PipId producer)
        {
            var result = Producer(operationId, "host file output");
            producer = result.PipId;
            return FindFile(result, path, operationId, "host file output");
        }

        public DirectoryArtifact GetProducedDirectory(ulong operationId, AbsolutePath path) =>
            FindDirectory(Producer(operationId, "host directory output"), path, operationId, "host directory output");

        public Task<GraphOperationResponse> HandleOperationAsync(GraphOperation operation, CancellationToken cancellationToken) =>
            HandleOperationAsync(operation, PipData.Invalid, cancellationToken);

        // Transitional text caller; removed when the text loader switches to native operations.
        public Task<GraphOperationResponse> HandleOperationAsync(GraphOperation operation, PipData data, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (m_traceOperation) Logger.Log.GraphOperationReceived(m_loggingContext, operation.ToString());
            GraphOperationResponse response;
            if (TryGetOperationCounters(operation.OperationCase, out var count, out var duration))
            {
                m_counters.IncrementCounter(count);
                using (m_counters.StartStopwatch(duration)) { response = HandleOperation(operation, data); }
            }
            else response = HandleOperation(operation, data);
            response.ProtocolVersion = DirectGraphProtocol.Version;
            if (m_traceOperation) Logger.Log.GraphOperationResponse(m_loggingContext, response.ToString());
            return Task.FromResult(response);
        }

        private GraphOperationResponse HandleOperation(GraphOperation operation, PipData data)
        {
            if (m_firstRejection is { } rejected)
            {
                return Failure(operation.OperationId, GraphOperationResponseStatus.PreviousOperationRejected,
                    $"Graph construction already failed at operation {rejected.OperationId}, so this operation was not processed. That operation was rejected because: {rejected.Error}");
            }
            if (operation.ProtocolVersion != DirectGraphProtocol.Version)
            {
                return Failure(operation.OperationId, GraphOperationResponseStatus.VersionMismatch, operation.ProtocolVersion == 0
                    ? $"The emitter did not set GraphOperation.protocol_version. This BuildXL speaks DirectGraph protocol version {DirectGraphProtocol.Version}."
                    : $"The emitter speaks DirectGraph protocol version {operation.ProtocolVersion}. This BuildXL speaks version {DirectGraphProtocol.Version}.");
            }
            try
            {
                Accept(operation);
                if (data.IsValid && !(operation.AddProcess != null ? operation.AddProcess.Arguments.Count == 0 :
                    operation.AddWriteFile != null && !operation.AddWriteFile.HasContent))
                {
                    throw new InvalidRequestException("Structured data requires a process with no wire-format arguments or a write-file operation with no wire-format content.");
                }
                if (operation.Complete != null)
                {
                    Volatile.Write(ref m_complete, true);
                    return Success(operation.OperationId);
                }
                var (module, symbol) = operation.OperationCase switch
                {
                    GraphOperation.OperationOneofCase.AddProcess => (operation.AddProcess.Module, operation.AddProcess.Symbol),
                    GraphOperation.OperationOneofCase.AddWriteFile => (operation.AddWriteFile.Module, operation.AddWriteFile.Symbol),
                    GraphOperation.OperationOneofCase.AddCopyFile => (operation.AddCopyFile.Module, operation.AddCopyFile.Symbol),
                    GraphOperation.OperationOneofCase.AddSealDirectory => (operation.AddSealDirectory.Module, operation.AddSealDirectory.Symbol),
                    GraphOperation.OperationOneofCase.AddCompositeSharedOpaqueDirectory => (operation.AddCompositeSharedOpaqueDirectory.Module, operation.AddCompositeSharedOpaqueDirectory.Symbol),
                    GraphOperation.OperationOneofCase.AddExport => (operation.AddExport.Module, (string)null),
                    _ => throw new InvalidRequestException("The graph operation is not specified."),
                };
                var helper = operation.AddExport == null ? PipConstructor.ResolveHelper(module, symbol) : null;
                var native = Translate(operation, data);
                native.Module = module;
                native.Symbol = symbol;
                var result = PipConstructor.Construct(native, helper, countOperation: false);
                m_producedArtifacts.Add(operation.OperationId, result);
                return Success(operation.OperationId, result.SemiStableHash);
            }
            catch (InvalidRequestException exception) { return Failure(operation.OperationId, GraphOperationResponseStatus.InvalidRequest, exception.Message); }
            catch (DirectGraphConstructionException exception) { return Failure(operation.OperationId, GraphOperationResponseStatus.GraphError, exception.Message); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Failure(operation.OperationId, GraphOperationResponseStatus.GraphError,
                    $"BuildXL could not construct the pip: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private DirectGraphOperation Translate(GraphOperation operation, PipData data)
        {
            switch (operation.OperationCase)
            {
                case GraphOperation.OperationOneofCase.AddProcess:
                    return TranslateProcess(operation.AddProcess, data);
                case GraphOperation.OperationOneofCase.AddWriteFile:
                    var write = operation.AddWriteFile;
                    var destination = RequiredPath(write.HasDestination, write.Destination, "destination");
                    var content = new PipDataBuilder(m_pathTable.StringTable);
                    if (!data.IsValid) content.Add(RequiredString(write.HasContent, write.Content, "content"));
                    return Common(new DirectGraphWriteFile
                    {
                        Destination = destination,
                        Content = data.IsValid ? data : content.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping),
                    }, write.Tags, write.HasDescription, write.Description);
                case GraphOperation.OperationOneofCase.AddCopyFile:
                    var copy = operation.AddCopyFile;
                    if (copy.Source == null) throw new InvalidRequestException("The copy-file operation does not specify a source.");
                    return Common(new DirectGraphCopyFile
                    {
                        Source = ResolveFile(copy.Source, "source"),
                        Destination = RequiredPath(copy.HasDestination, copy.Destination, "destination"),
                        OutputsMustRemainWritable = copy.OutputsMustRemainWritable,
                    }, copy.Tags, copy.HasDescription, copy.Description);
                case GraphOperation.OperationOneofCase.AddSealDirectory:
                    var seal = operation.AddSealDirectory;
                    var nativeSeal = Common(new DirectGraphSealDirectory
                    {
                        DirectoryRoot = RequiredPath(seal.HasDirectoryRoot, seal.DirectoryRoot, "directory_root"),
                        Kind = seal.Kind switch
                        {
                            SealDirectoryKind.Partial => NativeSealKind.Partial,
                            SealDirectoryKind.Full => NativeSealKind.Full,
                            SealDirectoryKind.SourceAllDirectories => NativeSealKind.SourceAllDirectories,
                            SealDirectoryKind.SourceTopDirectoryOnly => NativeSealKind.SourceTopDirectoryOnly,
                            _ => throw InvalidEnum((int)seal.Kind, "kind"),
                        },
                        Scrub = seal.Scrub,
                    }, seal.Tags, seal.HasDescription, seal.Description);
                    nativeSeal.Contents.AddRange(seal.Contents.Select(file => ResolveFile(file, "contents")));
                    nativeSeal.OutputDirectoryContents.AddRange(seal.OutputDirectoryContents.Select(directory => ResolveDirectory(directory, "output_directory_contents")));
                    nativeSeal.Patterns.AddRange(seal.Patterns.Select(id => ResolveString(id, "patterns")));
                    nativeSeal.SourceExclusions.AddRange(seal.SourceExclusions.Select(id => ResolveString(id, "source_exclusions")));
                    return nativeSeal;
                case GraphOperation.OperationOneofCase.AddCompositeSharedOpaqueDirectory:
                    var compose = operation.AddCompositeSharedOpaqueDirectory;
                    var root = RequiredPath(compose.HasDirectoryRoot, compose.DirectoryRoot, "directory_root");
                    var directories = compose.Contents.Select(directory => ResolveDirectory(directory, "contents")).ToArray();
                    PipConstructor.ValidateCompositeDirectories(directories);
                    if (compose.ContentFilter != null) DirectGraphPipConstructor.ValidateContentFilterRegex(compose.ContentFilter.Regex);
                    var nativeCompose = Common(new DirectGraphCompositeDirectory
                    {
                        DirectoryRoot = root,
                        Action = compose.Action switch
                        {
                            CompositionActionKind.WidenDirectoryCone => SealDirectoryCompositionActionKind.WidenDirectoryCone,
                            CompositionActionKind.NarrowDirectoryCone => SealDirectoryCompositionActionKind.NarrowDirectoryCone,
                            _ => throw InvalidEnum((int)compose.Action, "action"),
                        },
                        ContentFilter = compose.ContentFilter == null ? null : new SealDirectoryContentFilter(
                            compose.ContentFilter.Kind switch
                            {
                                ContentFilterKind.Include => SealDirectoryContentFilter.ContentFilterKind.Include,
                                ContentFilterKind.Exclude => SealDirectoryContentFilter.ContentFilterKind.Exclude,
                                _ => throw InvalidEnum((int)compose.ContentFilter.Kind, "content_filter.kind"),
                            }, compose.ContentFilter.Regex),
                    }, compose.Tags, compose.HasDescription, compose.Description);
                    nativeCompose.Contents.AddRange(directories);
                    return nativeCompose;
                case GraphOperation.OperationOneofCase.AddExport:
                    var export = new DirectGraphExport();
                    export.Files.AddRange(operation.AddExport.Files.Select(file => ResolveFile(file, "files")));
                    export.Directories.AddRange(operation.AddExport.Directories.Select(directory => ResolveDirectory(directory, "directories")));
                    return export;
                default: throw new InvalidRequestException("The graph operation is not specified.");
            }
        }

        private DirectGraphProcess TranslateProcess(AddProcessRequest request, PipData data)
        {
            if (request.Executable == null) throw new InvalidRequestException("The process operation does not specify an executable.");
            var native = Common(new DirectGraphProcess
            {
                Executable = ResolveFile(request.Executable, "executable"),
                WorkingDirectory = RequiredPath(request.HasWorkingDirectory, request.WorkingDirectory, "working_directory"),
                ToolDescription = OptionalString(request.HasToolDescription, request.ToolDescription, "tool_description"),
                AbsentPathProbeMode = !request.HasAbsentPathProbeInUndeclaredOpaqueMode ? null : request.AbsentPathProbeInUndeclaredOpaqueMode switch
                {
                    AbsentPathProbeMode.Unsafe => Process.AbsentPathProbeInUndeclaredOpaquesMode.Unsafe,
                    AbsentPathProbeMode.Strict => Process.AbsentPathProbeInUndeclaredOpaquesMode.Strict,
                    AbsentPathProbeMode.Relaxed => Process.AbsentPathProbeInUndeclaredOpaquesMode.Relaxed,
                    _ => throw InvalidEnum((int)request.AbsentPathProbeInUndeclaredOpaqueMode, "absent_path_probe_in_undeclared_opaque_mode"),
                },
                DoubleWritePolicy = !request.HasDoubleWritePolicy ? null : request.DoubleWritePolicy switch
                {
                    DoubleWritePolicy.DoubleWritesAreErrors => RewritePolicy.DoubleWritesAreErrors,
                    DoubleWritePolicy.AllowSameContentDoubleWrites => RewritePolicy.AllowSameContentDoubleWrites,
                    DoubleWritePolicy.UnsafeFirstDoubleWriteWins => RewritePolicy.UnsafeFirstDoubleWriteWins,
                    _ => throw InvalidEnum((int)request.DoubleWritePolicy, "double_write_policy"),
                },
                SourceRewritePolicy = !request.HasSourceRewritePolicy ? null : request.SourceRewritePolicy switch
                {
                    SourceRewritePolicy.SourceRewritesAreErrors => RewritePolicy.SourceRewritesAreErrors,
                    SourceRewritePolicy.SafeSourceRewritesAreAllowed => RewritePolicy.SafeSourceRewritesAreAllowed,
                    _ => throw InvalidEnum((int)request.SourceRewritePolicy, "source_rewrite_policy"),
                },
                Timeout = request.TimeoutMillis > 0 ? TimeSpan.FromMilliseconds(request.TimeoutMillis) : null,
                WarningTimeout = request.WarningTimeoutMillis > 0 ? TimeSpan.FromMilliseconds(request.WarningTimeoutMillis) : null,
                Weight = request.Weight, Priority = request.Priority, ProcessRetries = request.ProcessRetries,
                WarningRegex = OptionalString(request.HasWarningRegex, request.WarningRegex, "warning_regex"),
                ErrorRegex = OptionalString(request.HasErrorRegex, request.ErrorRegex, "error_regex"),
                EnableMultiLineErrorScanning = request.EnableMultiLineErrorScanning,
                StandardOutput = OptionalPath(request.HasStandardOutput, request.StandardOutput, "standard_output"),
                StandardError = OptionalPath(request.HasStandardError, request.StandardError, "standard_error"),
                TraceFile = OptionalPath(request.HasTraceFile, request.TraceFile, "trace_file"),
                TempDirectory = OptionalPath(request.HasTempDirectory, request.TempDirectory, "temp_directory"),
                EnableTempDirectory = request.EnableTempDirectory,
                RetryAttemptEnvironmentVariable = OptionalString(request.HasRetryAttemptEnvironmentVariable, request.RetryAttemptEnvironmentVariable, "retry_attempt_environment_variable"),
            }, request.Tags, request.HasDescription, request.Description);
            var arguments = new PipDataBuilder(m_pathTable.StringTable);
            if (data.IsValid) arguments.Add(data);
            else foreach (var id in request.Arguments) arguments.Add(ResolveString(id, "arguments"));
            native.Arguments = arguments.ToPipData(" ", PipDataFragmentEscaping.CRuntimeArgumentRules);
            if (request.Options != null)
            {
                foreach (var field in ProcessOptions.Descriptor.Fields.InDeclarationOrder())
                {
                    if ((bool)field.Accessor.GetValue(request.Options)) DirectGraphPipConstructor.SetProcessOption(native, field.JsonName);
                }
            }
            native.InputFiles.AddRange(request.InputFiles.Select(file => ResolveFile(file, "input_files")));
            native.InputDirectories.AddRange(request.InputDirectories.Select(directory => ResolveDirectory(directory, "input_directories")));
            foreach (var output in request.OutputFiles)
            {
                var existence = output.Existence switch
                {
                    FileExistenceKind.Required => FileExistence.Required,
                    FileExistenceKind.Temporary => FileExistence.Temporary,
                    FileExistenceKind.Optional => FileExistence.Optional,
                    _ => throw InvalidEnum((int)output.Existence, "output_files.existence"),
                };
                native.OutputFiles.Add(output.TargetCase switch
                {
                    OutputFile.TargetOneofCase.Path => new(ResolvePath(output.Path, "output_files"), existence),
                    OutputFile.TargetOneofCase.Rewrites => new(default, existence, ResolveProducedFile(output.Rewrites, "output_files")),
                    _ => throw new InvalidRequestException("'output_files' has an entry that sets neither 'path' nor 'rewrites'."),
                });
            }
            native.OutputDirectories.AddRange(request.OutputDirectories.Select(directory => new DirectGraphDirectoryOutput(
                RequiredPath(directory.HasPath, directory.Path, "output_directories"), directory.Kind switch
                {
                    OutputDirectoryKind.Shared => NativeSealKind.SharedOpaque,
                    OutputDirectoryKind.Exclusive => NativeSealKind.Opaque,
                    _ => throw InvalidEnum((int)directory.Kind, "output_directories.kind"),
                })));
            native.EnvironmentVariables.AddRange(request.EnvironmentVariables.Select(variable => new DirectGraphEnvironmentVariable(
                RequiredString(variable.HasName, variable.Name, "environment_variables.name"),
                variable.IsPassThrough ? default : RequiredString(variable.HasValue, variable.Value, "environment_variables.value"), variable.IsPassThrough)));
            native.Semaphores.AddRange(request.Semaphores.Select(semaphore => new DirectGraphSemaphore(
                RequiredString(semaphore.HasName, semaphore.Name, "semaphores.name"), semaphore.Limit, semaphore.IncrementBy)));
            native.UntrackedPaths.AddRange(request.UntrackedPaths.Select(id => ResolvePath(id, "untracked_paths")));
            native.UntrackedScopes.AddRange(request.UntrackedScopes.Select(id => ResolvePath(id, "untracked_scopes")));
            native.OutputDirectoryExclusions.AddRange(request.OutputDirectoryExclusions.Select(id => ResolvePath(id, "output_directory_exclusions")));
            native.AdditionalTempDirectories.AddRange(request.AdditionalTempDirectories.Select(id => ResolvePath(id, "additional_temp_directories")));
            native.AllowedUndeclaredSourceReadScopes.AddRange(request.AllowedUndeclaredSourceReadScopes.Select(id => ResolvePath(id, "allowed_undeclared_source_read_scopes")));
            native.AllowedUndeclaredSourceReadPaths.AddRange(request.AllowedUndeclaredSourceReadPaths.Select(id => ResolvePath(id, "allowed_undeclared_source_read_paths")));
            native.AllowedSurvivingChildProcessNames.AddRange(request.AllowedSurvivingChildProcessNames.Select(id => ResolveString(id, "allowed_surviving_child_process_names")));
            native.BreakawayChildProcesses.AddRange(request.BreakawayChildProcesses.Select(child => new DirectGraphBreakawayChildProcess(
                RequiredString(child.HasProcessName, child.ProcessName, "breakaway_child_processes.process_name"), child.RequiredArguments, child.RequiredArgumentsIgnoreCase)));
            native.SuccessExitCodes.AddRange(request.SuccessExitCodes);
            native.RetryExitCodes.AddRange(request.RetryExitCodes);
            native.SucceedFastExitCodes.AddRange(request.SucceedFastExitCodes);
            native.UncacheableExitCodes.AddRange(request.UncacheableExitCodes);
            foreach (var reference in request.OrderOnlyInputs)
            {
                if (reference.ReferenceCase != FileArtifactReference.ReferenceOneofCase.ProducedFile)
                    throw new InvalidRequestException("An order-only prerequisite must name a produced file.");
                native.OrderOnlyInputs.Add(new(ResolveProducedFile(reference.ProducedFile, "order_only_inputs"),
                    Producer(reference.ProducedFile.ProducerOperationId, "order_only_inputs").PipId));
            }
            return native;
        }

        private T Common<T>(T operation, IReadOnlyList<uint> tags, bool hasDescription, uint description) where T : DirectGraphOperation
        {
            operation.Description = OptionalString(hasDescription, description, "description");
            operation.Tags.AddRange(tags.Select(id => ResolveString(id, "tags")));
            return operation;
        }

        private void Accept(GraphOperation operation)
        {
            if (operation.OperationId == 0) throw new InvalidRequestException("The operation has no id. Operation ids must be non-zero.");
            if (m_producedArtifacts.ContainsKey(operation.OperationId))
                throw new InvalidRequestException($"Operation id {operation.OperationId} has already been used. Operation ids must be unique within a stream.");
            foreach (var registration in operation.NewPaths)
            {
                CheckNextId(registration, m_paths.Count, "new_paths");
                if (!AbsolutePath.TryCreate(m_pathTable, registration.Value, out var path))
                    throw new InvalidRequestException($"'{registration.Value}', registered as path id {registration.Id}, is not a valid absolute path.");
                m_paths.Add(path);
            }
            foreach (var registration in operation.NewStrings)
            {
                CheckNextId(registration, m_strings.Count, "new_strings");
                m_strings.Add(StringId.Create(m_pathTable.StringTable, registration.Value));
            }
        }
        private static void CheckNextId(Registration registration, int registered, string field)
        {
            if (registration.Id != (uint)registered)
                throw new InvalidRequestException($"'{field}' registers id {registration.Id}, but the next id is {registered}. The emitter's ids no longer match BuildXL's.");
        }
        private FileArtifact ResolveFile(FileArtifactReference reference, string field) => reference?.ReferenceCase switch
        {
            FileArtifactReference.ReferenceOneofCase.SourcePath => FileArtifact.CreateSourceFile(ResolvePath(reference.SourcePath, field)),
            FileArtifactReference.ReferenceOneofCase.ProducedFile => ResolveProducedFile(reference.ProducedFile, field),
            _ => throw new InvalidRequestException($"A file reference in '{field}' is not specified."),
        };
        private FileArtifact ResolveProducedFile(ProducedFileReference reference, string field)
        {
            var path = RequiredPath(reference.HasPath, reference.Path, field);
            return FindFile(Producer(reference.ProducerOperationId, field), path, reference.ProducerOperationId, field);
        }
        private FileArtifact FindFile(DirectGraphConstructionResult result, AbsolutePath path, ulong id, string field)
        {
            int index = Array.BinarySearch(result.Files, FileArtifact.CreateSourceFile(path), OrdinalPathOnlyFileArtifactComparer.Instance);
            if (index < 0) throw new InvalidRequestException($"'{field}' refers to file '{path.ToString(m_pathTable)}' of operation {id}, which produced no such file.");
            return result.Files[index];
        }
        private DirectoryArtifact ResolveDirectory(DirectoryArtifactReference reference, string field)
        {
            if (reference == null) throw new InvalidRequestException($"A directory reference in '{field}' is not specified.");
            var path = RequiredPath(reference.HasPath, reference.Path, field);
            return FindDirectory(Producer(reference.ProducerOperationId, field), path, reference.ProducerOperationId, field);
        }
        private DirectoryArtifact FindDirectory(DirectGraphConstructionResult result, AbsolutePath path, ulong id, string field)
        {
            foreach (var directory in result.Directories) if (directory.Path == path) return directory;
            throw new InvalidRequestException($"'{field}' refers to directory '{path.ToString(m_pathTable)}' of operation {id}, which produced no such directory.");
        }
        private DirectGraphConstructionResult Producer(ulong id, string field) => m_producedArtifacts.TryGetValue(id, out var result) ? result :
            throw new InvalidRequestException($"'{field}' refers to operation {id}, which BuildXL has not received. An operation must be sent after the operations it depends on.");
        private AbsolutePath ResolvePath(uint id, string field) => id < (uint)m_paths.Count ? m_paths[(int)id] :
            throw new InvalidRequestException($"'{field}' refers to path id {id}, but only {m_paths.Count} paths have been registered.");
        private AbsolutePath RequiredPath(bool present, uint id, string field) => present ? ResolvePath(id, field) : throw new InvalidRequestException($"'{field}' is required.");
        private AbsolutePath OptionalPath(bool present, uint id, string field) => present ? ResolvePath(id, field) : AbsolutePath.Invalid;
        private StringId ResolveString(uint id, string field) => id < (uint)m_strings.Count ? m_strings[(int)id] :
            throw new InvalidRequestException($"'{field}' refers to string id {id}, but only {m_strings.Count} strings have been registered.");
        private StringId RequiredString(bool present, uint id, string field) => present ? ResolveString(id, field) : throw new InvalidRequestException($"'{field}' is required.");
        private StringId OptionalString(bool present, uint id, string field) => present ? ResolveString(id, field) : StringId.Invalid;
        private static InvalidRequestException InvalidEnum(int value, string field) => new(value == 0
            ? $"'{field}' is required." : $"'{field}' has the value {value}, which this version of BuildXL does not know.");
        private static GraphOperationResponse Success(ulong id, long hash = 0) => new() { OperationId = id, Status = GraphOperationResponseStatus.Success, SemiStableHash = hash };
        private GraphOperationResponse Failure(ulong id, GraphOperationResponseStatus status, string error)
        {
            m_firstRejection ??= (id, error);
            return new() { OperationId = id, Status = status, Error = error };
        }
        private static bool TryGetOperationCounters(GraphOperation.OperationOneofCase kind, out DirectGraphCounter count, out DirectGraphCounter duration)
        {
            (count, duration) = kind switch
            {
                GraphOperation.OperationOneofCase.AddProcess => (DirectGraphCounter.AddProcessCount, DirectGraphCounter.AddProcessDuration),
                GraphOperation.OperationOneofCase.AddWriteFile => (DirectGraphCounter.AddWriteFileCount, DirectGraphCounter.AddWriteFileDuration),
                GraphOperation.OperationOneofCase.AddCopyFile => (DirectGraphCounter.AddCopyFileCount, DirectGraphCounter.AddCopyFileDuration),
                GraphOperation.OperationOneofCase.AddSealDirectory => (DirectGraphCounter.AddSealDirectoryCount, DirectGraphCounter.AddSealDirectoryDuration),
                GraphOperation.OperationOneofCase.AddCompositeSharedOpaqueDirectory => (DirectGraphCounter.AddCompositeSharedOpaqueDirectoryCount, DirectGraphCounter.AddCompositeSharedOpaqueDirectoryDuration),
                GraphOperation.OperationOneofCase.AddExport => (DirectGraphCounter.AddExportCount, DirectGraphCounter.AddExportDuration),
                GraphOperation.OperationOneofCase.Complete => (DirectGraphCounter.CompleteCount, DirectGraphCounter.CompleteDuration),
                _ => default,
            };
            return kind is >= GraphOperation.OperationOneofCase.AddProcess and <= GraphOperation.OperationOneofCase.AddExport;
        }
    }
}

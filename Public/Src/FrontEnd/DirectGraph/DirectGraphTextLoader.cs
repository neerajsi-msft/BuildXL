// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Engine.DirectGraph;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using SealDirectoryKind = BuildXL.Engine.DirectGraph.SealDirectoryKind;
using GraphSealDirectoryKind = BuildXL.Pips.Operations.SealDirectoryKind;

namespace BuildXL.FrontEnd.DirectGraph
{
    /// <summary>
    /// Expands one project's text fragments directly into its pip builder. Only artifact indexes and one operation
    /// are retained; neither a whole-graph object model nor a serialization/transport round trip is needed.
    /// </summary>
    public sealed class DirectGraphTextLoader
    {
        private static readonly StringComparer s_paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private readonly PathTable m_pathTable;
        private readonly Func<GraphOperation, PipData, CancellationToken, Task<GraphOperationResponse>> m_send;
        private readonly DirectGraphPipGraphBuilder m_builder;
        private readonly SharedState m_state;
        private readonly DirectGraphValueRegistry m_values = new();
        private readonly List<Project> m_dependencies = new();
        private readonly Dictionary<FileArtifact, ulong> m_importedFiles = new();
        private readonly Dictionary<DirectoryArtifact, ulong> m_importedDirectories = new();
        private readonly List<FileBinding> m_pendingFiles = new();
        private readonly List<DirectoryBinding> m_pendingDirectories = new();
        private Project m_project;
        private ulong m_nextId = 1;
        private ulong m_nextImportId = ulong.MaxValue;

        /// <nodoc />
        public DirectGraphTextLoader(
            PathTable pathTable, DirectGraphPipGraphBuilder builder, SharedState state,
            Func<GraphOperation, PipData, CancellationToken, Task<GraphOperationResponse>> send = null)
        {
            m_pathTable = pathTable;
            m_builder = builder;
            m_state = state;
            m_send = send ?? builder.HandleOperationAsync;
        }

        /// <summary>
        /// Resolver-wide ownership and policy metadata. Visibility comes exclusively from the shared linker's
        /// dependency outputs, not from these indexes. This state requires serial project construction.
        /// </summary>
        public sealed class SharedState
        {
            internal readonly Dictionary<string, FileBinding> Files = new(s_paths);
            internal readonly List<DirectoryBinding> Directories = new();
            internal readonly List<DirectoryBinding> SourceDirectories = new();
            internal readonly HashSet<string> DynamicRoots = new(s_paths);
            internal readonly HashSet<string> ConfiguredOutputs = new(s_paths);
            internal readonly Dictionary<FileArtifact, FileBinding> FileArtifacts = new();
            internal readonly Dictionary<(string Project, AbsolutePath Path), DirectoryBinding> DirectoryExports = new();

            /// <nodoc />
            public SharedState(PathTable pathTable, IReadOnlyList<IDirectGraphProjectSettings> settings)
            {
                foreach (var project in settings)
                {
                    ConfiguredOutputs.UnionWith((project.FileExports ?? Array.Empty<AbsolutePath>()).Select(path => path.ToString(pathTable)));
                }
            }
        }

        /// <summary>
        /// The opener must register each fragment as a tracked front-end input. The source probe must track existence,
        /// not read content, for source order-only prerequisites.
        /// </summary>
        public async Task<ProjectGraphOutputs> LoadProjectAsync(
            IDirectGraphProjectSettings settings,
            IReadOnlyDictionary<DirectGraphProject, ProjectGraphOutputs> dependencyOutputs,
            Func<AbsolutePath, TextReader> open,
            Func<AbsolutePath, bool> sourceExists,
            CancellationToken cancellationToken)
        {
            if (m_project != null)
            {
                throw new InvalidOperationException("A text loader constructs only one project.");
            }
            m_project = new Project(settings, m_pathTable);
            foreach (var dependency in dependencyOutputs)
            {
                var imported = new Project(dependency.Key.Settings, m_pathTable);
                foreach (var file in dependency.Value.Files.Values)
                {
                    imported.ExportedFiles.Add(file.Path.ToString(m_pathTable), m_state.FileArtifacts[file]);
                }
                foreach (var directory in dependency.Value.Directories.Values)
                {
                    var binding = m_state.DirectoryExports[(dependency.Key.Name, directory.Path)];
                    if (binding.Artifact != directory.Root)
                    {
                        throw new InvalidDataException($"Dependency '{dependency.Key.Name}' supplied a different directory artifact for '{directory.Path.ToString(m_pathTable)}'.");
                    }
                    imported.ExportedDirectories.Add(directory.Path.ToString(m_pathTable), binding);
                }
                m_dependencies.Add(imported);
            }
            var project = m_project;
            foreach (var file in (settings.GraphFiles ?? Array.Empty<AbsolutePath>()).Concat(settings.PolicyFiles ?? Array.Empty<AbsolutePath>()))
            {
                using var reader = open(file);
                foreach (var record in new DirectGraphTextReader(reader, file.ToString(m_pathTable)).Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (record.Name.StartsWith("__directgraph_", StringComparison.Ordinal) || !project.Names.Add(record.Name))
                        {
                            throw new InvalidDataException($"Duplicate or reserved operation name '{record.Name}'.");
                        }
                        if (project.ExportStarted && record.Kind != "export")
                        {
                            throw new InvalidDataException("Export records must follow all construction records in a project.");
                        }
                        var operation = CreateOperation(project, record, sourceExists, out var data);
                        if (operation != null)
                        {
                            await EmitAsync(operation, cancellationToken, data);
                        }
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new InvalidDataException($"{record.Location} ({project.Name}/{record.Name}): {exception.Message}", exception);
                    }
                }
            }
            var export = new AddExportRequest { Module = project.Name };
            foreach (var path in settings.FileExports ?? Array.Empty<AbsolutePath>())
            {
                if (PublishFile(project, path.ToString(m_pathTable)) is { } reference)
                {
                    export.Files.Add(reference);
                }
            }
            foreach (var path in settings.DirectoryExports ?? Array.Empty<AbsolutePath>())
            {
                if (PublishDirectory(project, new[] { path.ToString(m_pathTable) }) is { } reference)
                {
                    export.Directories.Add(reference);
                }
            }
            if (export.Files.Count + export.Directories.Count > 0)
            {
                await EmitAsync(new GraphOperation { AddExport = export }, cancellationToken);
            }
            await EmitAsync(new GraphOperation { Complete = new CompleteRequest() }, cancellationToken);
            foreach (var directory in project.ExportedDirectories.Values)
            {
                m_state.DirectoryExports.Add((project.Name, directory.Artifact.Path), directory);
            }
            return new ProjectGraphOutputs(
                project.ExportedFiles.Values.Select(file => file.Artifact),
                project.ExportedDirectories.Values.Select(directory => new StaticDirectory(
                    directory.Artifact, directory.Kind,
                    SortedReadOnlyArray<FileArtifact, OrdinalPathOnlyFileArtifactComparer>.SortUnsafe(
                        directory.Members.Values.Select(file => file.Artifact).ToArray(),
                        OrdinalPathOnlyFileArtifactComparer.Instance))));
        }

        private GraphOperation CreateOperation(Project project, DirectGraphTextRecord record, Func<AbsolutePath, bool> sourceExists, out PipData data)
        {
            data = PipData.Invalid;
            var fields = new Fields(record);
            ulong id = m_nextId;
            var operation = new GraphOperation();
            switch (record.Kind)
            {
                case "writeFile":
                    string destination = Absolute(project, fields.One("destination", required: true));
                    operation.AddWriteFile = new AddWriteFileRequest
                    {
                        Module = project.Name,
                        Symbol = record.Name,
                        Destination = m_values.Path(destination),
                    };
                    var content = fields.Rows("content");
                    if (content.Count != 1)
                    {
                        throw new InvalidDataException("Field 'content' must occur exactly once.");
                    }
                    if (content[0].Length == 1)
                    {
                        operation.AddWriteFile.Content = m_values.String(content[0][0]);
                    }
                    else
                    {
                        var builder = new PipDataBuilder(m_pathTable.StringTable);
                        foreach (string part in content[0])
                        {
                            builder.Add(part);
                        }
                        data = builder.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping);
                    }
                    Description(fields, value => operation.AddWriteFile.Description = value);
                    ClaimFile(project, destination, id);
                    break;
                case "copyFile":
                    var source = ResolveFile(project, Absolute(project, fields.One("source", required: true)), null);
                    destination = Absolute(project, fields.One("destination", required: true));
                    operation.AddCopyFile = new AddCopyFileRequest
                    {
                        Module = project.Name,
                        Symbol = record.Name,
                        Source = source,
                        Destination = m_values.Path(destination),
                    };
                    Description(fields, value => operation.AddCopyFile.Description = value);
                    ClaimFile(project, destination, id);
                    break;
                case "process":
                    operation.AddProcess = CreateProcess(project, record.Name, fields, sourceExists, out data);
                    break;
                case "sealDirectory":
                    string root = Absolute(project, fields.One("directoryRoot", required: true));
                    var seal = new AddSealDirectoryRequest
                    {
                        Module = project.Name,
                        Symbol = record.Name,
                        DirectoryRoot = m_values.Path(root),
                        Kind = (fields.One("sealKind") ?? "partial") switch
                        {
                            "partial" => SealDirectoryKind.Partial,
                            "full" => SealDirectoryKind.Full,
                            "sourceAllDirectories" => SealDirectoryKind.SourceAllDirectories,
                            "sourceTopDirectoryOnly" => SealDirectoryKind.SourceTopDirectoryOnly,
                            string kind => throw new InvalidDataException($"Unknown seal kind '{kind}'."),
                        },
                    };
                    var members = new Dictionary<string, FileBinding>(s_paths);
                    foreach (string path in fields.Values("content"))
                    {
                        string absolute = Absolute(project, path);
                        var reference = ResolveFile(project, absolute, null);
                        if (!members.TryAdd(absolute, Binding(project, absolute, reference)))
                        {
                            throw new InvalidDataException($"Duplicate seal content '{absolute}'.");
                        }
                        seal.Contents.Add(reference);
                    }
                    foreach (var values in fields.Rows("outputDirectory"))
                    {
                        seal.OutputDirectoryContents.Add(DirectoryReference(ResolveDirectory(project, values)));
                    }
                    seal.Patterns.Add(fields.Values("pattern").Select(m_values.String));
                    seal.SourceExclusions.Add(fields.Values("sourceExclusion").Select(m_values.String));
                    Description(fields, value => seal.Description = value);
                    bool sourceSeal = seal.Kind is SealDirectoryKind.SourceAllDirectories or SealDirectoryKind.SourceTopDirectoryOnly;
                    if (sourceSeal)
                    {
                        if (members.Count > 0 || seal.OutputDirectoryContents.Count > 0)
                        {
                            throw new InvalidDataException("Source seals cannot contain generated artifacts.");
                        }
                        if (m_state.Files.Keys.Concat(m_state.ConfiguredOutputs).Any(path => Within(path, root)) ||
                            m_state.Directories.Any(directory => directory.Dynamic && Overlaps(directory.Path, root)))
                        {
                            throw new InvalidDataException($"Source seal '{root}' overlaps generated outputs.");
                        }
                    }
                    AddDirectory(project, new DirectoryBinding(project, record.Name, root, id, false, sourceSeal,
                        members: members,
                        kind: seal.Kind switch
                        {
                            SealDirectoryKind.Partial => GraphSealDirectoryKind.Partial,
                            SealDirectoryKind.Full => GraphSealDirectoryKind.Full,
                            SealDirectoryKind.SourceAllDirectories => GraphSealDirectoryKind.SourceAllDirectories,
                            SealDirectoryKind.SourceTopDirectoryOnly => GraphSealDirectoryKind.SourceTopDirectoryOnly,
                            _ => throw new InvalidDataException($"Unknown seal kind '{seal.Kind}'."),
                        }));
                    operation.AddSealDirectory = seal;
                    break;
                case "composeSharedOpaque":
                    root = Absolute(project, fields.One("directoryRoot", required: true));
                    var compose = new AddCompositeSharedOpaqueDirectoryRequest
                    {
                        Module = project.Name,
                        Symbol = record.Name,
                        DirectoryRoot = m_values.Path(root),
                        Action = (fields.One("action") ?? "widen") switch
                        {
                            "widen" => CompositionActionKind.WidenDirectoryCone,
                            "narrow" => CompositionActionKind.NarrowDirectoryCone,
                            string action => throw new InvalidDataException($"Unknown composition action '{action}'."),
                        },
                    };
                    var composedProducers = new HashSet<(Project Owner, ulong Id)>();
                    foreach (var values in fields.Rows("directory"))
                    {
                        var directory = ResolveDirectory(project, values);
                        compose.Contents.Add(DirectoryReference(directory));
                        composedProducers.UnionWith(directory.ProducerIds);
                    }
                    string exclude = fields.One("excludeRegex");
                    string include = fields.One("includeRegex");
                    if (exclude != null && include != null)
                    {
                        throw new InvalidDataException("A composition cannot have both includeRegex and excludeRegex.");
                    }
                    if (exclude != null || include != null)
                    {
                        compose.ContentFilter = new ContentFilter
                        {
                            Kind = exclude != null ? ContentFilterKind.Exclude : ContentFilterKind.Include,
                            Regex = exclude ?? include,
                        };
                    }
                    Description(fields, value => compose.Description = value);
                    AddDirectory(project, new DirectoryBinding(project, record.Name, root, id, true, producers: composedProducers));
                    operation.AddCompositeSharedOpaqueDirectory = compose;
                    break;
                case "export":
                    project.ExportStarted = true;
                    var export = new AddExportRequest { Module = project.Name };
                    var exportFiles = fields.Values("file").ToArray();
                    var exportDirectories = fields.Rows("directory");
                    if (exportFiles.Length + exportDirectories.Count == 0)
                    {
                        throw new InvalidDataException("An export must publish at least one artifact.");
                    }
                    foreach (string path in exportFiles)
                    {
                        if (PublishFile(project, Absolute(project, path)) is { } reference)
                        {
                            export.Files.Add(reference);
                        }
                    }
                    foreach (var values in exportDirectories)
                    {
                        if (PublishDirectory(project, values) is { } reference)
                        {
                            export.Directories.Add(reference);
                        }
                    }
                    if (export.Files.Count + export.Directories.Count == 0)
                    {
                        operation = null;
                    }
                    else
                    {
                        operation.AddExport = export;
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unknown operation '{record.Kind}'.");
            }
            fields.EnsureConsumed();
            return operation;
        }

        private AddProcessRequest CreateProcess(Project project, string name, Fields fields, Func<AbsolutePath, bool> sourceExists, out PipData arguments)
        {
            var process = new AddProcessRequest
            {
                Module = project.Name,
                Symbol = name,
                WorkingDirectory = m_values.Path(Absolute(project, fields.One("workingDirectory", required: true))),
                Options = new ProcessOptions(),
            };
            foreach (var values in fields.Rows("inputDirectory"))
            {
                AddInputDirectory(process, ResolveDirectory(project, values));
            }
            process.Executable = ResolveFile(project, Absolute(project, fields.One("executable", required: true)), process);
            var argumentRows = fields.Rows("argument");
            arguments = PipData.Invalid;
            if (argumentRows.Any(row => row.Length > 1))
            {
                var builder = new PipDataBuilder(m_pathTable.StringTable);
                foreach (var row in argumentRows)
                {
                    using (builder.StartFragment(PipDataFragmentEscaping.NoEscaping, string.Empty))
                    {
                        // PipData escapes leaves, not whole nested groups. Escape the complete argument before
                        // factoring its rendered spelling so quotes and backslashes across chunks remain correct.
                        string escaped = CommandLineEscaping.EscapeAsCommandLineWord(string.Concat(row));
                        foreach (Match part in Regex.Matches(escaped, @"\s+|[""'`]|[^\s""'`]+"))
                        {
                            builder.Add(part.Value);
                        }
                    }
                }
                arguments = builder.ToPipData(" ", PipDataFragmentEscaping.CRuntimeArgumentRules);
            }
            else
            {
                process.Arguments.Add(argumentRows.SelectMany(row => row).Select(m_values.String));
            }
            Description(fields, value => process.Description = value);
            string temp = fields.One("tempDirectory");
            if (temp != null)
            {
                process.TempDirectory = m_values.Path(Absolute(project, temp));
            }
            string automaticTemp = fields.One("enableTempDirectory");
            if (automaticTemp != null)
            {
                process.EnableTempDirectory = automaticTemp switch
                {
                    "true" => true,
                    "false" => false,
                    _ => throw new InvalidDataException("enableTempDirectory expects true or false."),
                };
            }
            var semaphoreNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var values in fields.Rows("semaphore"))
            {
                Arity(values, 3);
                if (string.IsNullOrEmpty(values[0]) || !semaphoreNames.Add(values[0]) ||
                    !int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out int limit) || limit <= 0 ||
                    !int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out int increment) || increment <= 0 || increment > limit)
                {
                    throw new InvalidDataException("semaphore expects a unique nonempty name, positive limit and positive increment no greater than the limit.");
                }
                process.Semaphores.Add(new ProcessSemaphore { Name = m_values.String(values[0]), Limit = limit, IncrementBy = increment });
            }
            var environment = new HashSet<string>(StringComparer.Ordinal);
            foreach (var values in fields.Rows("environment"))
            {
                Arity(values, 2);
                if (string.IsNullOrEmpty(values[0]) || values[0].Contains('=') || !environment.Add(values[0]))
                {
                    throw new InvalidDataException($"Invalid or duplicate environment name '{values[0]}'.");
                }
                process.EnvironmentVariables.Add(new EnvironmentVariableEntry { Name = m_values.String(values[0]), Value = m_values.String(values[1]) });
            }
            foreach (string variable in fields.Values("passthroughEnvironment"))
            {
                if (string.IsNullOrEmpty(variable) || variable.Contains('=') || !environment.Add(variable))
                {
                    throw new InvalidDataException($"Invalid or duplicate passthrough environment name '{variable}'.");
                }
                process.EnvironmentVariables.Add(new EnvironmentVariableEntry { Name = m_values.String(variable), IsPassThrough = true });
            }
            foreach (string option in fields.Values("option"))
            {
                string jsonName = option == "dependsOnCurrentOs" ? "dependsOnCurrentHostOsDirectories" : option;
                var descriptor = ProcessOptions.Descriptor.Fields.InDeclarationOrder().FirstOrDefault(field => field.JsonName == jsonName);
                if (descriptor == null)
                {
                    throw new InvalidDataException($"Unknown process option '{option}'.");
                }
                descriptor.Accessor.SetValue(process.Options, true);
            }
            foreach (string path in fields.Values("input"))
            {
                var reference = ResolveFile(project, Absolute(project, path), process, allowDirectoryOnly: true);
                if (reference != null)
                {
                    process.InputFiles.Add(reference);
                }
            }
            foreach (string path in fields.Values("orderOnly"))
            {
                string absolute = Absolute(project, path);
                var reference = ResolveFile(project, absolute, null);
                if (reference.ReferenceCase == FileArtifactReference.ReferenceOneofCase.ProducedFile)
                {
                    process.OrderOnlyInputs.Add(reference);
                }
                else if (!sourceExists(AbsolutePath.Create(m_pathTable, absolute)))
                {
                    throw new InvalidDataException($"Missing source order-only prerequisite '{absolute}'.");
                }
            }
            foreach (string path in fields.Values("output"))
            {
                string absolute = Absolute(project, path);
                ClaimFile(project, absolute, m_nextId);
                process.OutputFiles.Add(new OutputFile { Path = m_values.Path(absolute), Existence = FileExistenceKind.Required });
            }
            foreach (string path in fields.Values("rewrite"))
            {
                string absolute = Absolute(project, path);
                var reference = ResolveFile(project, absolute, process);
                if (reference.ReferenceCase != FileArtifactReference.ReferenceOneofCase.ProducedFile ||
                    !project.Files.ContainsKey(absolute))
                {
                    throw new InvalidDataException($"A rewrite must name a previous local output: '{absolute}'.");
                }
                process.OutputFiles.Add(new OutputFile { Rewrites = reference.ProducedFile, Existence = FileExistenceKind.Required });
                ClaimFile(project, absolute, m_nextId, rewrite: true);
            }
            foreach (var values in fields.Rows("outputDirectory"))
            {
                if (values.Length is < 1 or > 2)
                {
                    throw new InvalidDataException("outputDirectory expects a path and optional shared/exclusive kind.");
                }
                string absolute = Absolute(project, values[0]);
                var kind = (values.Length == 1 ? "shared" : values[1]) switch
                {
                    "shared" => OutputDirectoryKind.Shared,
                    "exclusive" => OutputDirectoryKind.Exclusive,
                    string value => throw new InvalidDataException($"Unknown output directory kind '{value}'."),
                };
                AddDirectory(project, new DirectoryBinding(project, name, absolute, m_nextId, true,
                    kind: kind == OutputDirectoryKind.Shared ? GraphSealDirectoryKind.SharedOpaque : GraphSealDirectoryKind.Opaque));
                process.OutputDirectories.Add(new OutputDirectory { Path = m_values.Path(absolute), Kind = kind });
            }
            return process;
        }

        /// <summary>Returns null for an unlisted ordinary input whose dependency is supplied by an opaque directory.</summary>
        private FileArtifactReference ResolveFile(Project project, string path, AddProcessRequest process, bool allowDirectoryOnly = false)
        {
            if (project.Files.TryGetValue(path, out var local))
            {
                return FileReference(local);
            }
            var files = m_dependencies.Where(dependency => dependency.ExportedFiles.ContainsKey(path))
                .Select(dependency => dependency.ExportedFiles[path]).Distinct().ToArray();
            if (files.Length > 1)
            {
                throw new InvalidDataException($"Ambiguous exported file '{path}'.");
            }
            if (files.Length == 1)
            {
                return FileReference(files[0]);
            }
            var visible = VisibleDirectories(project, path).Distinct().ToArray();
            var members = visible.Where(directory => directory.Members.ContainsKey(path))
                .Select(directory => directory.Members[path]).Distinct().ToArray();
            if (members.Length > 1)
            {
                throw new InvalidDataException($"Ambiguous static directory member '{path}'.");
            }
            if (members.Length == 1)
            {
                return FileReference(members[0]);
            }
            var dynamic = visible.Where(directory => directory.Dynamic && Within(path, directory.Path)).ToArray();
            if (dynamic.Length > 0)
            {
                if (process != null && dynamic.Length > 1)
                {
                    dynamic = dynamic.Where(directory => process.InputDirectories.Any(reference =>
                        reference.ProducerOperationId == DirectoryReference(directory).ProducerOperationId &&
                        reference.Path == m_values.Path(directory.Path))).ToArray();
                }
                if (dynamic.Length != 1 || process == null)
                {
                    throw new InvalidDataException($"Dynamic member '{path}' needs one unambiguous directory and a process consumer; use an explicit inputDirectory for overlapping roots.");
                }
                AddInputDirectory(process, dynamic[0]);
                if (m_state.Files.TryGetValue(path, out var generated))
                {
                    if (!dynamic[0].ProducerIds.Contains((generated.Owner, generated.Id)))
                    {
                        throw new InvalidDataException($"Opaque export does not expose private explicit file '{path}'.");
                    }
                    return FileReference(generated);
                }
                if (!allowDirectoryOnly)
                {
                    throw new InvalidDataException($"Unlisted opaque member '{path}' requires a declared output where a concrete file artifact is needed.");
                }
                return null;
            }
            if (m_state.Files.ContainsKey(path) || m_state.ConfiguredOutputs.Contains(path) || Ancestors(path).Any(m_state.DynamicRoots.Contains))
            {
                throw new InvalidDataException($"Generated input '{path}' is not exported by a declared dependency.");
            }
            return new FileArtifactReference { SourcePath = m_values.Path(path) };
        }

        private DirectoryBinding ResolveDirectory(Project project, string[] values)
        {
            if (values.Length is < 1 or > 2)
            {
                throw new InvalidDataException("A directory reference expects a root and optional producer name (project/operation for dependencies).");
            }
            string path = Absolute(project, values[0]);
            var directories = (project.Directories.TryGetValue(path, out var local) ? local : Enumerable.Empty<DirectoryBinding>())
                .Concat(m_dependencies.Where(dependency => dependency.ExportedDirectories.ContainsKey(path))
                    .Select(dependency => dependency.ExportedDirectories[path])).Distinct();
            if (values.Length == 2)
            {
                directories = directories.Where(directory => values[1] == directory.Owner.Name + "/" + directory.Symbol ||
                    (directory.Owner == project && values[1] == directory.Symbol));
            }
            var candidates = directories.ToArray();
            if (candidates.Length != 1)
            {
                throw new InvalidDataException($"Directory '{path}' has {candidates.Length} visible producers; specify its exact producer when ambiguous.");
            }
            return candidates[0];
        }

        private FileArtifactReference PublishFile(Project project, string path)
        {
            var reference = ResolveFile(project, path, null);
            var binding = Binding(project, path, reference);
            if (binding.Id == 0 || project.ExportedDirectories.ContainsKey(path))
            {
                throw new InvalidDataException($"Export '{path}' must name a produced file and cannot conflict with a directory export.");
            }
            if (project.ExportedFiles.TryGetValue(path, out var previous))
            {
                if (previous.Artifact != binding.Artifact)
                {
                    throw new InvalidDataException($"Conflicting file export identities at '{path}'.");
                }
                return null;
            }
            project.ExportedFiles.Add(path, binding);
            return reference;
        }

        private DirectoryArtifactReference PublishDirectory(Project project, string[] values)
        {
            var directory = ResolveDirectory(project, values);
            if (project.ExportedFiles.ContainsKey(directory.Path))
            {
                throw new InvalidDataException($"Directory export '{directory.Path}' conflicts with a file export.");
            }
            if (project.ExportedDirectories.TryGetValue(directory.Path, out var previous))
            {
                if (previous.Artifact != directory.Artifact)
                {
                    throw new InvalidDataException($"Conflicting directory export identities at '{directory.Path}'.");
                }
                return null;
            }
            project.ExportedDirectories.Add(directory.Path, directory);
            return DirectoryReference(directory);
        }

        private void ClaimFile(Project project, string path, ulong id, bool rewrite = false)
        {
            if (m_state.SourceDirectories.Any(directory => Within(path, directory.Path)))
            {
                throw new InvalidDataException($"Generated output '{path}' overlaps a source seal.");
            }
            if (m_state.Files.TryGetValue(path, out var previous) && (!rewrite || previous.Owner != project))
            {
                throw new InvalidDataException($"Duplicate file ownership at '{path}'.");
            }
            var binding = new FileBinding(project, path, id);
            m_state.Files[path] = binding;
            project.Files[path] = binding;
            m_pendingFiles.Add(binding);
        }

        private void AddDirectory(Project project, DirectoryBinding directory)
        {
            if (directory.Dynamic && m_state.SourceDirectories.Any(source => Overlaps(source.Path, directory.Path)))
            {
                throw new InvalidDataException($"Generated directory '{directory.Path}' overlaps a source seal.");
            }
            if (!project.Directories.TryGetValue(directory.Path, out var directories))
            {
                project.Directories.Add(directory.Path, directories = new List<DirectoryBinding>());
            }
            directories.Add(directory);
            m_state.Directories.Add(directory);
            m_pendingDirectories.Add(directory);
            if (directory.Source)
            {
                m_state.SourceDirectories.Add(directory);
            }
            if (directory.Dynamic)
            {
                m_state.DynamicRoots.Add(directory.Path);
            }
        }

        private FileBinding Binding(Project project, string path, FileArtifactReference reference) =>
            reference.ReferenceCase == FileArtifactReference.ReferenceOneofCase.ProducedFile
                ? m_state.FileArtifacts[m_builder.GetProducedFile(
                    reference.ProducedFile.ProducerOperationId, AbsolutePath.Create(m_pathTable, path), out _)]
                : new FileBinding(project, path, 0) { Artifact = FileArtifact.CreateSourceFile(AbsolutePath.Create(m_pathTable, path)) };

        private FileArtifactReference FileReference(FileBinding binding)
        {
            if (binding.Id == 0)
            {
                return new FileArtifactReference { SourcePath = m_values.Path(binding.Path) };
            }
            ulong id = binding.Id;
            if (binding.Owner != m_project && !m_importedFiles.TryGetValue(binding.Artifact, out id))
            {
                id = m_nextImportId--;
                m_builder.RegisterImportedArtifacts(id, new[] { binding.Artifact }, Array.Empty<DirectoryArtifact>(), binding.Producer);
                m_importedFiles.Add(binding.Artifact, id);
            }
            return new FileArtifactReference { ProducedFile = new ProducedFileReference { ProducerOperationId = id, Path = m_values.Path(binding.Path) } };
        }

        private DirectoryArtifactReference DirectoryReference(DirectoryBinding binding)
        {
            ulong id = binding.Id;
            if (binding.Owner != m_project && !m_importedDirectories.TryGetValue(binding.Artifact, out id))
            {
                id = m_nextImportId--;
                m_builder.RegisterImportedArtifacts(id, Array.Empty<FileArtifact>(), new[] { binding.Artifact });
                m_importedDirectories.Add(binding.Artifact, id);
            }
            return new DirectoryArtifactReference { ProducerOperationId = id, Path = m_values.Path(binding.Path) };
        }

        private void AddInputDirectory(AddProcessRequest process, DirectoryBinding directory)
        {
            uint path = m_values.Path(directory.Path);
            var reference = DirectoryReference(directory);
            if (!process.InputDirectories.Any(input => input.ProducerOperationId == reference.ProducerOperationId && input.Path == path))
            {
                process.InputDirectories.Add(reference);
            }
        }

        private IEnumerable<DirectoryBinding> VisibleDirectories(Project project, string path)
        {
            foreach (string ancestor in Ancestors(path))
            {
                if (project.Directories.TryGetValue(ancestor, out var local))
                {
                    foreach (var directory in local)
                    {
                        yield return directory;
                    }
                }
                foreach (var dependency in m_dependencies)
                {
                    if (dependency.ExportedDirectories.TryGetValue(ancestor, out var directory))
                    {
                        yield return directory;
                    }
                }
            }
        }

        private static IEnumerable<string> Ancestors(string path)
        {
            for (string ancestor = path; ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
            {
                yield return ancestor;
            }
        }

        private static string Absolute(Project project, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            {
                throw new InvalidDataException("Artifact paths must be nonempty and contain no NUL.");
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, project.Root));
        }

        private static bool Within(string path, string root) =>
            s_paths.Equals(path, root) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static bool Overlaps(string a, string b) => Within(a, b) || Within(b, a);

        private void Description(Fields fields, Action<uint> set)
        {
            string value = fields.One("description");
            if (value != null)
            {
                set(m_values.String(value));
            }
        }

        private async Task EmitAsync(GraphOperation operation, CancellationToken cancellationToken, PipData data = default)
        {
            operation.OperationId = m_nextId++;
            operation.ProtocolVersion = DirectGraphProtocol.Version;
            m_values.AttachNewValues(operation);
            var response = await m_send(operation, data, cancellationToken);
            if (response.Status != GraphOperationResponseStatus.Success)
            {
                throw new InvalidDataException($"Operation {operation.OperationId} rejected: {response.Error}");
            }
            foreach (var file in m_pendingFiles)
            {
                file.Artifact = m_builder.GetProducedFile(file.Id, AbsolutePath.Create(m_pathTable, file.Path), out var producer);
                file.Producer = producer;
                m_state.FileArtifacts.Add(file.Artifact, file);
            }
            foreach (var directory in m_pendingDirectories)
            {
                directory.Artifact = m_builder.GetProducedDirectory(directory.Id, AbsolutePath.Create(m_pathTable, directory.Path));
            }
            m_pendingFiles.Clear();
            m_pendingDirectories.Clear();
        }

        private static void Arity(string[] values, int count)
        {
            if (values.Length != count)
            {
                throw new InvalidDataException($"Expected {count} field value(s), found {values.Length}.");
            }
        }

        private sealed class Fields
        {
            private readonly DirectGraphTextRecord m_record;
            public Fields(DirectGraphTextRecord record) => m_record = record;
            public IReadOnlyList<string[]> Rows(string name) =>
                m_record.Fields.Remove(name, out var rows) ? rows : Array.Empty<string[]>();
            public IEnumerable<string> Values(string name)
            {
                var rows = Rows(name);
                if (name is not ("input" or "orderOnly"))
                {
                    foreach (var row in rows)
                    {
                        Arity(row, 1);
                    }
                }
                return rows.SelectMany(row => row);
            }
            public string One(string name, bool required = false)
            {
                var rows = Rows(name);
                if (rows.Count == 0 && !required)
                {
                    return null;
                }
                if (rows.Count != 1)
                {
                    throw new InvalidDataException($"Field '{name}' must occur exactly once.");
                }
                Arity(rows[0], 1);
                return rows[0][0];
            }
            public void EnsureConsumed()
            {
                if (m_record.Fields.Count != 0)
                {
                    throw new InvalidDataException($"Unknown field(s): {string.Join(", ", m_record.Fields.Keys)}.");
                }
            }
        }

        internal sealed class Project
        {
            public string Name { get; }
            public string Root { get; }
            public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, FileBinding> Files { get; } = new(s_paths);
            public Dictionary<string, List<DirectoryBinding>> Directories { get; } = new(s_paths);
            public Dictionary<string, FileBinding> ExportedFiles { get; } = new(s_paths);
            public Dictionary<string, DirectoryBinding> ExportedDirectories { get; } = new(s_paths);
            public bool ExportStarted { get; set; }
            public Project(IDirectGraphProjectSettings settings, PathTable pathTable)
            {
                Name = settings.Name.ToString(pathTable.StringTable);
                Root = settings.Root.ToString(pathTable);
            }
        }

        internal sealed record FileBinding(Project Owner, string Path, ulong Id)
        {
            public FileArtifact Artifact { get; set; }
            public PipId Producer { get; set; }
        }

        internal sealed class DirectoryBinding
        {
            public Project Owner { get; }
            public string Symbol { get; }
            public string Path { get; }
            public ulong Id { get; }
            public bool Dynamic { get; }
            public bool Source { get; }
            public HashSet<(Project Owner, ulong Id)> ProducerIds { get; }
            public DirectoryArtifact Artifact { get; set; }
            public GraphSealDirectoryKind Kind { get; }
            public Dictionary<string, FileBinding> Members { get; }
            public DirectoryBinding(Project owner, string symbol, string path, ulong id, bool dynamic,
                bool source = false, Dictionary<string, FileBinding> members = null,
                HashSet<(Project Owner, ulong Id)> producers = null, GraphSealDirectoryKind? kind = null)
            {
                Owner = owner;
                Symbol = symbol;
                Path = path;
                Id = id;
                Dynamic = dynamic;
                Source = source;
                ProducerIds = producers ?? new HashSet<(Project Owner, ulong Id)> { (owner, id) };
                Kind = kind ?? (dynamic ? GraphSealDirectoryKind.SharedOpaque : GraphSealDirectoryKind.Partial);
                Members = members ?? new Dictionary<string, FileBinding>(s_paths);
            }
        }
    }
}
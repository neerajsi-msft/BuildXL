// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
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
        private readonly Func<DirectGraphOperation, CancellationToken, Task<DirectGraphConstructionResult>> m_construct;
        private readonly DirectGraphPipConstructor m_constructor;
        private readonly SharedState m_state;
        private readonly List<Project> m_dependencies = new();
        private readonly List<FileBinding> m_pendingFiles = new();
        private readonly List<DirectoryBinding> m_pendingDirectories = new();
        private Project m_project;
        private DirectGraphTextRecord m_record;
        private string m_sourceConsumer;
        private string SourceConsumer => m_sourceConsumer ??= m_record != null
            ? $"{m_record.Location} ({m_project.Name}/{m_record.Name})"
            : $"Project '{m_project.Name}' configured exports";

        /// <nodoc />
        public DirectGraphTextLoader(
            PathTable pathTable, DirectGraphPipConstructor constructor, SharedState state,
            Func<DirectGraphOperation, CancellationToken, Task<DirectGraphConstructionResult>> construct = null)
        {
            m_pathTable = pathTable;
            m_constructor = constructor;
            m_state = state;
            m_construct = construct ?? ((operation, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(m_constructor.Construct(operation));
            });
        }

        /// <summary>
        /// Resolver-wide ownership and policy metadata. Visibility comes exclusively from the shared linker's
        /// dependency outputs, not from these indexes. Only ownership checks/claims hold SyncRoot;
        /// parsing, native pip construction and per-project value/producer tables remain independent.
        /// </summary>
        public sealed class SharedState
        {
            internal readonly object SyncRoot = new();
            internal readonly Dictionary<string, FileBinding> Files = new(s_paths);
            internal readonly List<DirectoryBinding> Directories = new();
            internal readonly List<DirectoryBinding> SourceDirectories = new();
            internal readonly HashSet<string> DynamicRoots = new(s_paths);
            internal readonly Dictionary<string, string> ExplicitTempDirectories = new(s_paths);
            internal readonly Dictionary<string, string> SourceFiles = new(s_paths);
            internal readonly Dictionary<string, string> SourceCones = new(s_paths);
            internal readonly Dictionary<string, List<(DirectoryBinding Directory, string Consumer)>> OpaqueFileReads = new(s_paths);
            // Frozen before project tasks start, including exports of projects not selected for construction.
            internal readonly HashSet<string> ConfiguredOutputs = new(s_paths);
            internal readonly ConcurrentDictionary<FileArtifact, FileBinding> FileArtifacts = new();
            internal readonly ConcurrentDictionary<(string Project, AbsolutePath Path), DirectoryBinding> DirectoryExports = new();

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
                        m_record = record;
                        m_sourceConsumer = null;
                        var operation = CreateOperation(project, record, sourceExists);
                        if (operation != null)
                        {
                            await EmitAsync(operation, cancellationToken);
                        }
                    }
                    catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or DirectGraphInvalidRequestException or DirectGraphConstructionException)
                    {
                        throw new InvalidDataException($"{record.Location} ({project.Name}/{record.Name}): {exception.Message}", exception);
                    }
                }
            }
            m_record = null;
            m_sourceConsumer = null;
            var export = new DirectGraphExport { Module = project.Name };
            try
            {
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
                    await EmitAsync(export, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or DirectGraphInvalidRequestException or DirectGraphConstructionException)
            {
                throw new InvalidDataException($"{SourceConsumer}: {exception.Message}", exception);
            }
            foreach (var directory in project.ExportedDirectories.Values)
            {
                if (!m_state.DirectoryExports.TryAdd((project.Name, directory.Artifact.Path), directory))
                {
                    throw new InvalidDataException($"Project '{project.Name}' registered a directory export twice.");
                }
            }
            return new ProjectGraphOutputs(
                project.ExportedFiles.Values.Select(file => file.Artifact),
                project.ExportedDirectories.Values.Select(directory => new StaticDirectory(
                    directory.Artifact, directory.Kind,
                    SortedReadOnlyArray<FileArtifact, OrdinalPathOnlyFileArtifactComparer>.SortUnsafe(
                        directory.Members.Values.Select(file => file.Artifact).ToArray(),
                        OrdinalPathOnlyFileArtifactComparer.Instance))));
        }

        private DirectGraphOperation CreateOperation(Project project, DirectGraphTextRecord record, Func<AbsolutePath, bool> sourceExists)
        {
            var fields = new Fields(record);
            DirectGraphOperation operation;
            switch (record.Kind)
            {
                case "writeFile":
                    string destination = Absolute(project, fields.One("destination", required: true));
                    var write = new DirectGraphWriteFile
                    {
                        Module = project.Name,
                        Symbol = record.Name,
                        Destination = PathValue(destination),
                    };
                    var content = fields.Rows("content");
                    if (content.Count != 1)
                    {
                        throw new InvalidDataException("Field 'content' must occur exactly once.");
                    }
                    var contentBuilder = new PipDataBuilder(m_pathTable.StringTable);
                    foreach (string part in content[0]) contentBuilder.Add(part);
                    write.Content = contentBuilder.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping);
                    Description(fields, value => write.Description = value);
                    ClaimFile(project, destination, record.Name);
                    operation = write;
                    break;
                case "copyFile":
                    var source = ResolveFile(project, Absolute(project, fields.One("source", required: true)), null);
                    destination = Absolute(project, fields.One("destination", required: true));
                    var copy = new DirectGraphCopyFile
                    {
                        Module = project.Name,
                        Symbol = record.Name,
                        Source = source.Artifact,
                        Destination = PathValue(destination),
                    };
                    Description(fields, value => copy.Description = value);
                    ClaimFile(project, destination, record.Name);
                    operation = copy;
                    break;
                case "process":
                    operation = CreateProcess(project, record.Name, fields, sourceExists);
                    break;
                case "sealDirectory":
                    string root = Absolute(project, fields.One("directoryRoot", required: true));
                    var seal = new DirectGraphSealDirectory
                    {
                        Module = project.Name,
                        Symbol = record.Name,
                        DirectoryRoot = PathValue(root),
                        Kind = (fields.One("sealKind") ?? "partial") switch
                        {
                            "partial" => GraphSealDirectoryKind.Partial,
                            "full" => GraphSealDirectoryKind.Full,
                            "sourceAllDirectories" => GraphSealDirectoryKind.SourceAllDirectories,
                            "sourceTopDirectoryOnly" => GraphSealDirectoryKind.SourceTopDirectoryOnly,
                            string kind => throw new InvalidDataException($"Unknown seal kind '{kind}'."),
                        },
                    };
                    var members = new Dictionary<string, FileBinding>(s_paths);
                    foreach (string path in fields.Values("content"))
                    {
                        string absolute = Absolute(project, path);
                        var reference = ResolveFile(project, absolute, null);
                        if (!members.TryAdd(absolute, reference))
                        {
                            throw new InvalidDataException($"Duplicate seal content '{absolute}'.");
                        }
                        seal.Contents.Add(reference.Artifact);
                    }
                    foreach (var values in fields.Rows("outputDirectory"))
                    {
                        seal.OutputDirectoryContents.Add(ResolveDirectory(project, values).Artifact);
                    }
                    seal.Patterns.AddRange(fields.Values("pattern").Select(StringValue));
                    seal.SourceExclusions.AddRange(fields.Values("sourceExclusion").Select(StringValue));
                    Description(fields, value => seal.Description = value);
                    bool sourceSeal = seal.Kind.IsSourceSeal();
                    if (sourceSeal)
                    {
                        if (members.Count > 0 || seal.OutputDirectoryContents.Count > 0)
                        {
                            throw new InvalidDataException("Source seals cannot contain generated artifacts.");
                        }
                    }
                    AddDirectory(project, new DirectoryBinding(project, record.Name, root, false, sourceSeal, members: members, kind: seal.Kind));
                    operation = seal;
                    break;
                case "composeSharedOpaque":
                    root = Absolute(project, fields.One("directoryRoot", required: true));
                    var compose = new DirectGraphCompositeDirectory
                    {
                        Module = project.Name,
                        Symbol = record.Name,
                        DirectoryRoot = PathValue(root),
                        Action = (fields.One("action") ?? "widen") switch
                        {
                            "widen" => SealDirectoryCompositionActionKind.WidenDirectoryCone,
                            "narrow" => SealDirectoryCompositionActionKind.NarrowDirectoryCone,
                            string action => throw new InvalidDataException($"Unknown composition action '{action}'."),
                        },
                    };
                    var composedProducers = new HashSet<(Project Owner, string Symbol)>();
                    foreach (var values in fields.Rows("directory"))
                    {
                        var directory = ResolveDirectory(project, values);
                        compose.Contents.Add(directory.Artifact);
                        composedProducers.UnionWith(directory.Producers);
                    }
                    string exclude = fields.One("excludeRegex");
                    string include = fields.One("includeRegex");
                    if (exclude != null && include != null)
                    {
                        throw new InvalidDataException("A composition cannot have both includeRegex and excludeRegex.");
                    }
                    if (exclude != null || include != null)
                    {
                        compose.ContentFilter = new SealDirectoryContentFilter(exclude != null
                            ? SealDirectoryContentFilter.ContentFilterKind.Exclude : SealDirectoryContentFilter.ContentFilterKind.Include, exclude ?? include);
                    }
                    Description(fields, value => compose.Description = value);
                    AddDirectory(project, new DirectoryBinding(project, record.Name, root, true, producers: composedProducers));
                    operation = compose;
                    break;
                case "export":
                    project.ExportStarted = true;
                    var export = new DirectGraphExport { Module = project.Name };
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
                        operation = export;
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unknown operation '{record.Kind}'.");
            }
            fields.EnsureConsumed();
            return operation;
        }

        private DirectGraphProcess CreateProcess(Project project, string name, Fields fields, Func<AbsolutePath, bool> sourceExists)
        {
            var process = new DirectGraphProcess
            {
                Module = project.Name,
                Symbol = name,
                WorkingDirectory = PathValue(Absolute(project, fields.One("workingDirectory", required: true))),
            };
            foreach (string path in fields.Values("output"))
            {
                string absolute = Absolute(project, path);
                ClaimFile(project, absolute, name);
                process.OutputFiles.Add(new DirectGraphFileOutput(PathValue(absolute)));
            }
            foreach (var values in fields.Rows("inputDirectory"))
            {
                AddInputDirectory(process, ResolveDirectory(project, values));
            }
            process.Executable = ResolveFile(project, Absolute(project, fields.One("executable", required: true)), process).Artifact;
            var argumentRows = fields.Rows("argument");
            var arguments = new PipDataBuilder(m_pathTable.StringTable);
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
                arguments.Add(builder.ToPipData(" ", PipDataFragmentEscaping.CRuntimeArgumentRules));
            }
            else
            {
                foreach (string value in argumentRows.SelectMany(row => row)) arguments.Add(value);
            }
            process.Arguments = arguments.ToPipData(" ", PipDataFragmentEscaping.CRuntimeArgumentRules);
            Description(fields, value => process.Description = value);
            string temp = fields.One("tempDirectory");
            if (temp != null)
            {
                string path = Absolute(project, temp);
                ClaimTempDirectory(project, name, path);
                process.TempDirectory = PathValue(path);
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
            foreach (var values in fields.Rows("semaphore"))
            {
                Arity(values, 3);
                if (!int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out int limit) ||
                    !int.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out int increment))
                {
                    throw new InvalidDataException("semaphore expects a unique nonempty name, positive limit and positive increment no greater than the limit.");
                }
                process.Semaphores.Add(new DirectGraphSemaphore(StringValue(values[0]), limit, increment));
            }
            var environment = new HashSet<string>(StringComparer.Ordinal);
            foreach (var values in fields.Rows("environment"))
            {
                Arity(values, 2);
                if (string.IsNullOrEmpty(values[0]) || values[0].Contains('=') || !environment.Add(values[0]))
                {
                    throw new InvalidDataException($"Invalid or duplicate environment name '{values[0]}'.");
                }
                process.EnvironmentVariables.Add(new DirectGraphEnvironmentVariable(StringValue(values[0]), StringValue(values[1])));
            }
            foreach (string variable in fields.Values("passthroughEnvironment"))
            {
                if (string.IsNullOrEmpty(variable) || variable.Contains('=') || !environment.Add(variable))
                {
                    throw new InvalidDataException($"Invalid or duplicate passthrough environment name '{variable}'.");
                }
                process.EnvironmentVariables.Add(new DirectGraphEnvironmentVariable(StringValue(variable), IsPassThrough: true));
            }
            foreach (string option in fields.Values("option"))
            {
                DirectGraphPipConstructor.SetProcessOption(process, option);
            }
            foreach (string path in fields.Values("input"))
            {
                var reference = ResolveFile(project, Absolute(project, path), process, allowDirectoryOnly: true);
                if (reference != null)
                {
                    process.InputFiles.Add(reference.Artifact);
                }
            }
            foreach (string path in fields.Values("orderOnly"))
            {
                string absolute = Absolute(project, path);
                var reference = ResolveFile(project, absolute, null);
                if (!reference.Artifact.IsSourceFile)
                {
                    process.OrderOnlyInputs.Add(new DirectGraphOrderDependency(reference.Artifact, reference.Producer));
                }
                else if (!sourceExists(AbsolutePath.Create(m_pathTable, absolute)))
                {
                    throw new InvalidDataException($"Missing source order-only prerequisite '{absolute}'.");
                }
            }
            foreach (string path in fields.Values("rewrite"))
            {
                string absolute = Absolute(project, path);
                var reference = ResolveFile(project, absolute, process);
                if (reference.Artifact.IsSourceFile ||
                    !project.Files.ContainsKey(absolute))
                {
                    throw new InvalidDataException($"A rewrite must name a previous local output: '{absolute}'.");
                }
                process.OutputFiles.Add(new DirectGraphFileOutput(default, Rewrites: reference.Artifact));
                ClaimFile(project, absolute, name, rewrite: true);
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
                    "shared" => GraphSealDirectoryKind.SharedOpaque,
                    "exclusive" => GraphSealDirectoryKind.Opaque,
                    string value => throw new InvalidDataException($"Unknown output directory kind '{value}'."),
                };
                AddDirectory(project, new DirectoryBinding(project, name, absolute, true, kind: kind));
                process.OutputDirectories.Add(new DirectGraphDirectoryOutput(PathValue(absolute), kind));
            }
            return process;
        }

        /// <summary>Returns null for an unlisted ordinary input whose dependency is supplied by an opaque directory.</summary>
        private FileBinding ResolveFile(Project project, string path, DirectGraphProcess process, bool allowDirectoryOnly = false)
        {
            if (project.Files.TryGetValue(path, out var local))
            {
                if (!local.Artifact.IsValid)
                {
                    throw new InvalidDataException($"Input '{path}' refers to an output of the same operation.");
                }
                return local;
            }
            var files = m_dependencies.Where(dependency => dependency.ExportedFiles.ContainsKey(path))
                .Select(dependency => dependency.ExportedFiles[path]).Distinct().ToArray();
            if (files.Length > 1)
            {
                throw new InvalidDataException($"Ambiguous exported file '{path}'.");
            }
            if (files.Length == 1)
            {
                return files[0];
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
                return members[0];
            }
            var dynamic = visible.Where(directory => directory.Dynamic && Within(path, directory.Path)).ToArray();
            if (dynamic.Length > 0)
            {
                if (process != null && dynamic.Length > 1)
                {
                    dynamic = dynamic.Where(directory => process.InputDirectories.Contains(directory.Artifact)).ToArray();
                }
                if (dynamic.Length != 1 || process == null)
                {
                    throw new InvalidDataException($"Dynamic member '{path}' needs one unambiguous directory and a process consumer; use an explicit inputDirectory for overlapping roots.");
                }
                AddInputDirectory(process, dynamic[0]);
                lock (m_state.SyncRoot)
                {
                    if (m_state.Files.TryGetValue(path, out var generated))
                    {
                        if (!dynamic[0].Producers.Contains((generated.Owner, generated.Symbol)))
                        {
                            throw new InvalidDataException($"Opaque export does not expose private explicit file '{path}'.");
                        }
                        return generated;
                    }
                    if (!allowDirectoryOnly)
                    {
                        throw new InvalidDataException($"Unlisted opaque member '{path}' requires a declared output where a concrete file artifact is needed.");
                    }
                    if (!m_state.OpaqueFileReads.TryGetValue(path, out var reads))
                    {
                        m_state.OpaqueFileReads.Add(path, reads = new());
                    }
                    reads.Add((dynamic[0], SourceConsumer));
                    return null;
                }
            }
            lock (m_state.SyncRoot)
            {
                if (m_state.Files.ContainsKey(path) || m_state.ConfiguredOutputs.Contains(path) || Ancestors(path).Any(m_state.DynamicRoots.Contains))
                {
                    throw new InvalidDataException($"Generated input '{path}' is not exported by a declared dependency.");
                }
                if (!m_state.SourceFiles.ContainsKey(path))
                {
                    m_state.SourceFiles.Add(path, SourceConsumer);
                    foreach (string ancestor in Ancestors(path))
                    {
                        m_state.SourceCones.TryAdd(ancestor, path);
                    }
                }
                var absolute = PathValue(path);
                return new FileBinding(project, path, null, absolute) { Artifact = FileArtifact.CreateSourceFile(absolute) };
            }
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

        private FileArtifact? PublishFile(Project project, string path)
        {
            var reference = ResolveFile(project, path, null);
            var binding = reference;
            if (binding.Artifact.IsSourceFile || project.ExportedDirectories.ContainsKey(path))
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
            return reference.Artifact;
        }

        private DirectoryArtifact? PublishDirectory(Project project, string[] values)
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
            return directory.Artifact;
        }

        private void ClaimTempDirectory(Project project, string name, string path)
        {
            lock (m_state.SyncRoot)
            {
                ClaimTempDirectoryCore(project, name, path);
            }
        }

        private void ClaimTempDirectoryCore(Project project, string name, string path)
        {
            EnsureNoTempOverlap(path, "Explicit temp directory");
            if (m_state.Files.Keys.Any(output => Overlaps(path, output)) ||
                m_state.DynamicRoots.Any(root => Overlaps(path, root)))
            {
                throw new InvalidDataException($"Explicit temp directory '{path}' overlaps a claimed generated output.");
            }
            m_state.ExplicitTempDirectories.Add(path, $"{project.Name}/{name}");
        }

        private void EnsureNoTempOverlap(string path, string kind)
        {
            foreach (var claim in m_state.ExplicitTempDirectories)
            {
                if (Overlaps(path, claim.Key))
                {
                    throw new InvalidDataException($"{kind} '{path}' overlaps explicit temp directory '{claim.Key}' claimed by '{claim.Value}'.");
                }
            }
        }

        private void EnsureNoSourceBinding(string path, bool directory)
        {
            // Index one witness per ancestor so an output claim does not scan every source while holding SyncRoot.
            string source = directory ? m_state.SourceCones.GetValueOrDefault(path) :
                m_state.SourceFiles.ContainsKey(path) ? path : null;
            if (source != null)
            {
                throw new InvalidDataException($"Generated input '{source}' is not exported by a declared dependency; previously bound as a source by {m_state.SourceFiles[source]}.");
            }
        }

        private void ClaimFile(Project project, string path, string symbol, bool rewrite = false)
        {
            lock (m_state.SyncRoot)
            {
                ClaimFileCore(project, path, symbol, rewrite);
            }
        }

        private void ClaimFileCore(Project project, string path, string symbol, bool rewrite)
        {
            EnsureNoSourceBinding(path, directory: false);
            if (m_state.OpaqueFileReads.TryGetValue(path, out var reads))
            {
                foreach (var read in reads)
                {
                    if (!read.Directory.Producers.Contains((project, symbol)))
                    {
                        throw new InvalidDataException($"Opaque export does not expose private explicit file '{path}'; previously requested by {read.Consumer}.");
                    }
                }
            }
            EnsureNoTempOverlap(path, "Generated output");
            if (m_state.SourceDirectories.Any(directory => Within(path, directory.Path)))
            {
                throw new InvalidDataException($"Generated output '{path}' overlaps a source seal.");
            }
            if (m_state.Files.TryGetValue(path, out var previous) && (!rewrite || previous.Owner != project))
            {
                throw new InvalidDataException($"Duplicate file ownership at '{path}'.");
            }
            var binding = new FileBinding(project, path, symbol, PathValue(path));
            m_state.Files[path] = binding;
            project.Files[path] = binding;
            m_pendingFiles.Add(binding);
        }

        private void AddDirectory(Project project, DirectoryBinding directory)
        {
            directory.AbsolutePath = PathValue(directory.Path);
            lock (m_state.SyncRoot)
            {
                AddDirectoryCore(project, directory);
            }
        }

        private void AddDirectoryCore(Project project, DirectoryBinding directory)
        {
            if (directory.Source &&
                (m_state.Files.Keys.Concat(m_state.ConfiguredOutputs).Any(path => Within(path, directory.Path)) ||
                 m_state.Directories.Any(output => output.Dynamic && Overlaps(output.Path, directory.Path))))
            {
                throw new InvalidDataException($"Source seal '{directory.Path}' overlaps generated outputs.");
            }
            if (directory.Dynamic)
            {
                EnsureNoSourceBinding(directory.Path, directory: true);
                EnsureNoTempOverlap(directory.Path, "Generated directory");
            }
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

        private static void AddInputDirectory(DirectGraphProcess process, DirectoryBinding directory)
        {
            if (!process.InputDirectories.Contains(directory.Artifact))
            {
                process.InputDirectories.Add(directory.Artifact);
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

        private AbsolutePath PathValue(string value) => AbsolutePath.Create(m_pathTable, value);
        private StringId StringValue(string value) => StringId.Create(m_pathTable.StringTable, value);

        private void Description(Fields fields, Action<StringId> set)
        {
            string value = fields.One("description");
            if (value != null)
            {
                set(StringValue(value));
            }
        }

        private async Task EmitAsync(DirectGraphOperation operation, CancellationToken cancellationToken)
        {
            var result = await m_construct(operation, cancellationToken);
            foreach (var file in m_pendingFiles)
            {
                int index = Array.BinarySearch(result.Files, FileArtifact.CreateSourceFile(file.AbsolutePath), OrdinalPathOnlyFileArtifactComparer.Instance);
                if (index < 0)
                {
                    throw new InvalidDataException($"Native construction did not produce the declared file '{file.Path}'.");
                }
                file.Artifact = result.Files[index];
                file.Producer = result.PipId;
                if (!m_state.FileArtifacts.TryAdd(file.Artifact, file))
                {
                    throw new InvalidDataException($"Duplicate produced artifact at '{file.Path}'.");
                }
            }
            foreach (var directory in m_pendingDirectories)
            {
                int index = Array.BinarySearch(result.Directories, DirectoryArtifact.CreateWithZeroPartialSealId(directory.AbsolutePath),
                    DirectGraphConstructionResult.DirectoryPathComparer);
                if (index < 0)
                {
                    throw new InvalidDataException($"Native construction did not produce the declared directory '{directory.Path}'.");
                }
                directory.Artifact = result.Directories[index];
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

        internal sealed record FileBinding(Project Owner, string Path, string Symbol, AbsolutePath AbsolutePath)
        {
            public FileArtifact Artifact { get; set; }
            public PipId Producer { get; set; }
        }

        internal sealed class DirectoryBinding
        {
            public Project Owner { get; }
            public string Symbol { get; }
            public string Path { get; }
            public AbsolutePath AbsolutePath { get; set; }
            public bool Dynamic { get; }
            public bool Source { get; }
            public HashSet<(Project Owner, string Symbol)> Producers { get; }
            public DirectoryArtifact Artifact { get; set; }
            public GraphSealDirectoryKind Kind { get; }
            public Dictionary<string, FileBinding> Members { get; }
            public DirectoryBinding(Project owner, string symbol, string path, bool dynamic,
                bool source = false, Dictionary<string, FileBinding> members = null,
                HashSet<(Project Owner, string Symbol)> producers = null, GraphSealDirectoryKind? kind = null)
            {
                Owner = owner;
                Symbol = symbol;
                Path = path;
                Dynamic = dynamic;
                Source = source;
                Producers = producers ?? new HashSet<(Project Owner, string Symbol)> { (owner, symbol) };
                Kind = kind ?? (dynamic ? GraphSealDirectoryKind.SharedOpaque : GraphSealDirectoryKind.Partial);
                Members = members ?? new Dictionary<string, FileBinding>(s_paths);
            }
        }
    }
}
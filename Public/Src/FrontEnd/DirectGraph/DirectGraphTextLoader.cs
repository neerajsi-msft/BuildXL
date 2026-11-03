// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Engine.DirectGraph;
using BuildXL.Native.IO;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using SealDirectoryKind = BuildXL.Engine.DirectGraph.SealDirectoryKind;

namespace BuildXL.FrontEnd.DirectGraph
{
    /// <summary>
    /// Links dependency-first text fragments directly into the pip builder. Only artifact indexes and one operation
    /// are retained; neither a whole-graph object model nor a serialization/transport round trip is needed.
    /// </summary>
    public sealed class DirectGraphTextLoader
    {
        private static readonly StringComparer s_paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private readonly PathTable m_pathTable;
        private readonly Func<GraphOperation, PipData, CancellationToken, Task<GraphOperationResponse>> m_send;
        private readonly DirectGraphValueRegistry m_values = new();
        private readonly Dictionary<string, Project> m_projects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FileBinding> m_files = new(s_paths);
        private readonly List<DirectoryBinding> m_directories = new();
        private readonly List<DirectoryBinding> m_sourceDirectories = new();
        private readonly HashSet<string> m_dynamicRoots = new(s_paths);
        private readonly HashSet<string> m_configuredOutputs = new(s_paths);
        private ulong m_nextId = 1;

        /// <nodoc />
        public DirectGraphTextLoader(PathTable pathTable, Func<GraphOperation, PipData, CancellationToken, Task<GraphOperationResponse>> send)
        {
            m_pathTable = pathTable;
            m_send = send;
        }

        /// <summary>Validates the entire DAG, then returns the selected closure in dependency order.</summary>
        public static IReadOnlyList<IDirectGraphProjectSettings> OrderProjects(
            IReadOnlyList<IDirectGraphProjectSettings> projects, IReadOnlyList<string> roots, PathTable pathTable)
        {
            if (projects == null || projects.Count == 0)
            {
                throw new InvalidDataException("At least one text project is required.");
            }
            var byName = new Dictionary<string, IDirectGraphProjectSettings>(StringComparer.Ordinal);
            var uniqueNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects)
            {
                if (project == null || !project.Name.IsValid || !project.Root.IsValid)
                {
                    throw new InvalidDataException("Each text project requires a valid name and root.");
                }
                string name = project.Name.ToString(pathTable.StringTable);
                if (!uniqueNames.Add(name))
                {
                    throw new InvalidDataException($"Duplicate project name '{name}' (names must also differ ignoring case).");
                }
                byName.Add(name, project);
                if ((project.GraphFiles ?? Array.Empty<AbsolutePath>()).Concat(project.PolicyFiles ?? Array.Empty<AbsolutePath>()).Any(file => !file.IsValid) ||
                    (project.FileExports ?? Array.Empty<AbsolutePath>()).Concat(project.DirectoryExports ?? Array.Empty<AbsolutePath>()).Any(path => !path.IsValid))
                {
                    throw new InvalidDataException($"Project '{name}' contains an invalid fragment or export path.");
                }
            }
            var states = new Dictionary<string, int>(StringComparer.Ordinal);
            var ordered = new List<IDirectGraphProjectSettings>();
            void visit(string name)
            {
                if (name == null || !byName.TryGetValue(name, out var project))
                {
                    throw new InvalidDataException($"Unknown project '{name}'.");
                }
                if (states.TryGetValue(name, out int state))
                {
                    if (state == 1)
                    {
                        throw new InvalidDataException($"Project dependency cycle at '{name}'.");
                    }
                    return;
                }
                states[name] = 1;
                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                foreach (string dependency in project.Dependencies ?? Array.Empty<string>())
                {
                    if (dependency == null || !dependencies.Add(dependency))
                    {
                        throw new InvalidDataException($"Project '{name}' has an empty or repeated dependency.");
                    }
                    visit(dependency);
                }
                states[name] = 2;
                ordered.Add(project);
            }
            foreach (string name in byName.Keys)
            {
                visit(name);
            }
            var selected = new HashSet<string>(StringComparer.Ordinal);
            void select(string name)
            {
                if (name == null || !byName.TryGetValue(name, out var project))
                {
                    throw new InvalidDataException($"Unknown root project '{name}'.");
                }
                if (selected.Add(name))
                {
                    foreach (string dependency in project.Dependencies ?? Array.Empty<string>())
                    {
                        select(dependency);
                    }
                }
            }
            foreach (string name in roots ?? byName.Keys.ToArray())
            {
                select(name);
            }
            return ordered.Where(project => selected.Contains(project.Name.ToString(pathTable.StringTable))).ToArray();
        }

        /// <summary>
        /// The opener must register each fragment as a tracked front-end input. The source probe must track existence,
        /// not read content, for source order-only prerequisites.
        /// </summary>
        public async Task LoadAsync(
            IReadOnlyList<IDirectGraphProjectSettings> projects,
            IReadOnlyList<string> roots,
            Func<AbsolutePath, TextReader> open,
            Func<AbsolutePath, bool> sourceExists,
            CancellationToken cancellationToken)
        {
            var ordered = OrderProjects(projects, roots, m_pathTable);
            foreach (var project in projects)
            {
                m_configuredOutputs.UnionWith((project.FileExports ?? Array.Empty<AbsolutePath>()).Select(path => path.ToString(m_pathTable)));
            }
            foreach (var settings in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var project = new Project(settings, m_pathTable);
                m_projects.Add(project.Name, project);
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
                            await EmitAsync(operation, cancellationToken, data);
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
                    export.Files.Add(PublishFile(project, path.ToString(m_pathTable)));
                }
                foreach (var path in settings.DirectoryExports ?? Array.Empty<AbsolutePath>())
                {
                    export.Directories.Add(PublishDirectory(project, new[] { path.ToString(m_pathTable) }));
                }
                if (export.Files.Count + export.Directories.Count > 0)
                {
                    await EmitAsync(new GraphOperation { AddExport = export }, cancellationToken);
                }
            }
            await EmitAsync(new GraphOperation { Complete = new CompleteRequest() }, cancellationToken);
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
                        seal.Contents.Add(reference);
                        members.Add(absolute, Binding(project, absolute, reference));
                    }
                    foreach (var values in fields.Rows("outputDirectory"))
                    {
                        seal.OutputDirectoryContents.Add(DirectoryReference(ResolveDirectory(project, values)));
                    }
                    seal.Patterns.Add(fields.Values("pattern").Select(m_values.String));
                    Description(fields, value => seal.Description = value);
                    bool sourceSeal = seal.Kind is SealDirectoryKind.SourceAllDirectories or SealDirectoryKind.SourceTopDirectoryOnly;
                    if (sourceSeal)
                    {
                        if (members.Count > 0 || seal.OutputDirectoryContents.Count > 0)
                        {
                            throw new InvalidDataException("Source seals cannot contain generated artifacts.");
                        }
                        if (m_files.Keys.Concat(m_configuredOutputs).Any(path => Within(path, root)) ||
                            m_directories.Any(directory => directory.Dynamic && Overlaps(directory.Path, root)))
                        {
                            throw new InvalidDataException($"Source seal '{root}' overlaps generated outputs.");
                        }
                    }
                    AddDirectory(project, new DirectoryBinding(project, record.Name, root, id, false, sourceSeal,
                        seal.Kind == SealDirectoryKind.SourceTopDirectoryOnly, fields.TakenPatterns, members));
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
                    var composedProducers = new HashSet<ulong>();
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
                    foreach (string path in fields.Values("file"))
                    {
                        export.Files.Add(PublishFile(project, Absolute(project, path)));
                    }
                    foreach (var values in fields.Rows("directory"))
                    {
                        export.Directories.Add(PublishDirectory(project, values));
                    }
                    if (export.Files.Count + export.Directories.Count == 0)
                    {
                        throw new InvalidDataException("An export must publish at least one artifact.");
                    }
                    operation.AddExport = export;
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
                process.InputFiles.Add(ResolveFile(project, Absolute(project, path), process));
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
                AddDirectory(project, new DirectoryBinding(project, name, absolute, m_nextId, true));
                process.OutputDirectories.Add(new OutputDirectory { Path = m_values.Path(absolute), Kind = kind });
            }
            return process;
        }

        private FileArtifactReference ResolveFile(Project project, string path, AddProcessRequest process)
        {
            if (project.Files.TryGetValue(path, out var local))
            {
                return FileReference(local);
            }
            var files = Dependencies(project).Where(dependency => dependency.ExportedFiles.ContainsKey(path))
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
                    dynamic = dynamic.Where(directory => process.InputDirectories.Any(reference => reference.ProducerOperationId == directory.Id &&
                        reference.Path == m_values.Path(directory.Path))).ToArray();
                }
                if (dynamic.Length != 1 || process == null)
                {
                    throw new InvalidDataException($"Dynamic member '{path}' needs one unambiguous directory and a process consumer; use an explicit inputDirectory for overlapping roots.");
                }
                AddInputDirectory(process, dynamic[0]);
                if (m_files.TryGetValue(path, out var generated))
                {
                    if (!dynamic[0].ProducerIds.Contains(generated.Id))
                    {
                        throw new InvalidDataException($"Opaque export does not expose private explicit file '{path}'.");
                    }
                    return FileReference(generated);
                }
                return new FileArtifactReference { SourcePath = m_values.Path(path) };
            }
            if (m_files.ContainsKey(path) || m_configuredOutputs.Contains(path) || Ancestors(path).Any(m_dynamicRoots.Contains))
            {
                throw new InvalidDataException($"Generated input '{path}' is not exported by a declared dependency.");
            }
            var source = visible.Where(directory => directory.AllowsSource(path)).ToArray();
            if (source.Length == 0)
            {
                throw new InvalidDataException($"Unresolved input '{path}': no visible output or explicit source seal authorizes it.");
            }
            if (process != null)
            {
                foreach (var directory in source)
                {
                    AddInputDirectory(process, directory);
                }
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
                .Concat(Dependencies(project).Where(dependency => dependency.ExportedDirectories.ContainsKey(path))
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
            if (binding.Id == 0 || project.ExportedDirectories.ContainsKey(path) || !project.ExportedFiles.TryAdd(path, binding))
            {
                throw new InvalidDataException($"Export '{path}' must be a unique produced file.");
            }
            return reference;
        }

        private DirectoryArtifactReference PublishDirectory(Project project, string[] values)
        {
            var directory = ResolveDirectory(project, values);
            if (project.ExportedFiles.ContainsKey(directory.Path) || !project.ExportedDirectories.TryAdd(directory.Path, directory))
            {
                throw new InvalidDataException($"Duplicate export '{directory.Path}'.");
            }
            return DirectoryReference(directory);
        }

        private void ClaimFile(Project project, string path, ulong id, bool rewrite = false)
        {
            if (m_sourceDirectories.Any(directory => Within(path, directory.Path)))
            {
                throw new InvalidDataException($"Generated output '{path}' overlaps a source seal.");
            }
            if (m_files.TryGetValue(path, out var previous) && (!rewrite || previous.Owner != project))
            {
                throw new InvalidDataException($"Duplicate file ownership at '{path}'.");
            }
            var binding = new FileBinding(project, path, id);
            m_files[path] = binding;
            project.Files[path] = binding;
        }

        private void AddDirectory(Project project, DirectoryBinding directory)
        {
            if (directory.Dynamic && m_sourceDirectories.Any(source => Overlaps(source.Path, directory.Path)))
            {
                throw new InvalidDataException($"Generated directory '{directory.Path}' overlaps a source seal.");
            }
            if (!project.Directories.TryGetValue(directory.Path, out var directories))
            {
                project.Directories.Add(directory.Path, directories = new List<DirectoryBinding>());
            }
            directories.Add(directory);
            m_directories.Add(directory);
            if (directory.Source)
            {
                m_sourceDirectories.Add(directory);
            }
            if (directory.Dynamic)
            {
                m_dynamicRoots.Add(directory.Path);
            }
        }

        private FileBinding Binding(Project project, string path, FileArtifactReference reference) =>
            reference.ReferenceCase == FileArtifactReference.ReferenceOneofCase.ProducedFile
                ? new FileBinding(m_files[path].Owner, path, reference.ProducedFile.ProducerOperationId) : new FileBinding(project, path, 0);

        private FileArtifactReference FileReference(FileBinding binding) => binding.Id == 0
            ? new FileArtifactReference { SourcePath = m_values.Path(binding.Path) }
            : new FileArtifactReference { ProducedFile = new ProducedFileReference { ProducerOperationId = binding.Id, Path = m_values.Path(binding.Path) } };

        private DirectoryArtifactReference DirectoryReference(DirectoryBinding binding) =>
            new() { ProducerOperationId = binding.Id, Path = m_values.Path(binding.Path) };

        private void AddInputDirectory(AddProcessRequest process, DirectoryBinding directory)
        {
            uint path = m_values.Path(directory.Path);
            if (!process.InputDirectories.Any(reference => reference.ProducerOperationId == directory.Id && reference.Path == path))
            {
                process.InputDirectories.Add(DirectoryReference(directory));
            }
        }

        private IEnumerable<Project> Dependencies(Project project) =>
            (project.Settings.Dependencies ?? Array.Empty<string>()).Select(name => m_projects[name]);

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
                foreach (var dependency in Dependencies(project))
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
            public string[] TakenPatterns { get; private set; } = Array.Empty<string>();
            public Fields(DirectGraphTextRecord record) => m_record = record;
            public IReadOnlyList<string[]> Rows(string name) =>
                m_record.Fields.Remove(name, out var rows) ? rows : Array.Empty<string[]>();
            public IEnumerable<string> Values(string name)
            {
                var rows = Rows(name);
                if (name == "pattern")
                {
                    TakenPatterns = rows.SelectMany(row => row).ToArray();
                }
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

        private sealed class Project
        {
            public IDirectGraphProjectSettings Settings { get; }
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
                Settings = settings;
                Name = settings.Name.ToString(pathTable.StringTable);
                Root = settings.Root.ToString(pathTable);
            }
        }

        private sealed record FileBinding(Project Owner, string Path, ulong Id);

        private sealed class DirectoryBinding
        {
            public Project Owner { get; }
            public string Symbol { get; }
            public string Path { get; }
            public ulong Id { get; }
            public bool Dynamic { get; }
            public bool Source { get; }
            public HashSet<ulong> ProducerIds { get; }
            private readonly bool m_topOnly;
            private readonly string[] m_patterns;
            public Dictionary<string, FileBinding> Members { get; }
            public DirectoryBinding(Project owner, string symbol, string path, ulong id, bool dynamic,
                bool source = false, bool topOnly = false, string[] patterns = null, Dictionary<string, FileBinding> members = null,
                HashSet<ulong> producers = null)
            {
                Owner = owner;
                Symbol = symbol;
                Path = path;
                Id = id;
                Dynamic = dynamic;
                Source = source;
                ProducerIds = producers ?? new HashSet<ulong> { id };
                m_topOnly = topOnly;
                m_patterns = patterns ?? Array.Empty<string>();
                Members = members ?? new Dictionary<string, FileBinding>(s_paths);
            }
            public bool AllowsSource(string path) => Source && Within(path, Path) && !s_paths.Equals(path, Path) &&
                (!m_topOnly || s_paths.Equals(System.IO.Path.GetDirectoryName(path), Path)) &&
                (m_patterns.Length == 0 || m_patterns.Any(pattern =>
                    FileUtilities.PathMatchPattern(System.IO.Path.GetFileName(path), pattern)));
        }
    }
}
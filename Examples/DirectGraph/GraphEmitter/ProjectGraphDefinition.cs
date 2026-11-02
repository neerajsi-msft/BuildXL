// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GraphEmitter
{
    /// <summary>
    /// Links project-owned textual fragments before opening the construction stream.
    /// Artifact references retain their original producer; project ordering is not an execution barrier.
    /// </summary>
    internal sealed class ProjectGraphDefinition
    {
        private static readonly StringComparer s_pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        private readonly JsonSerializer m_serializer = JsonSerializer.Create(new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
        private readonly Dictionary<string, Project> m_projects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> m_fileOwners = new(s_pathComparer);
        private readonly List<(string Root, string Owner)> m_dynamicDirectories = new();
        private readonly Dictionary<string, Dictionary<string, JObject>> m_directoryMembers = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_dynamicProducers = new(StringComparer.Ordinal);

        public static GraphDefinition Load(string file)
        {
            var json = ReadObject(file);
            if (json["modules"] != null && json["formatVersion"] == null)
            {
                return json.ToObject<GraphDefinition>();
            }

            CheckVersion(json);
            var builder = new ProjectGraphDefinition();
            return builder.Link(Path.GetFullPath(file), json.ToObject<Manifest>(builder.m_serializer));
        }

        private GraphDefinition Link(string manifestFile, Manifest manifest)
        {
            foreach (var definition in manifest.Projects)
            {
                if (string.IsNullOrWhiteSpace(definition.Name) || !m_projects.TryAdd(definition.Name, new Project(definition)))
                {
                    throw new InvalidDataException($"Duplicate or empty project name '{definition.Name}'.");
                }
            }

            var ordered = new List<Project>();
            var states = new Dictionary<string, int>(StringComparer.Ordinal);
            void visit(string name)
            {
                if (!m_projects.TryGetValue(name, out var project))
                {
                    throw new InvalidDataException($"Unknown dependency project '{name}'.");
                }
                if (states.TryGetValue(name, out int state))
                {
                    if (state == 1)
                    {
                        throw new InvalidDataException($"Project dependency cycle involving '{name}'.");
                    }
                    return;
                }
                states[name] = 1;
                var dependencies = new HashSet<string>(StringComparer.Ordinal);
                foreach (string dependency in project.Definition.Dependencies)
                {
                    if (!dependencies.Add(dependency))
                    {
                        throw new InvalidDataException($"Project '{name}' repeats dependency '{dependency}'.");
                    }
                    visit(dependency);
                }
                states[name] = 2;
                ordered.Add(project);
            }

            // Validate the complete project DAG, including projects excluded by root selection.
            foreach (string name in m_projects.Keys)
            {
                visit(name);
            }
            var selected = new HashSet<string>(StringComparer.Ordinal);
            void select(string name)
            {
                if (!m_projects.TryGetValue(name, out var project))
                {
                    throw new InvalidDataException($"Unknown root project '{name}'.");
                }
                if (selected.Add(name))
                {
                    foreach (string dependency in project.Definition.Dependencies)
                    {
                        select(dependency);
                    }
                }
            }
            foreach (string root in manifest.Roots ?? m_projects.Keys.ToArray())
            {
                select(root);
            }

            foreach (var project in ordered)
            {
                string fragmentFile = Path.GetFullPath(project.Definition.Fragment, Path.GetDirectoryName(manifestFile));
                var json = ReadObject(fragmentFile);
                CheckVersion(json);
                project.Fragment = json.ToObject<Fragment>(m_serializer);
                project.Root = Path.GetFullPath(project.Definition.Root ?? project.Fragment.Root ?? ".", Path.GetDirectoryName(fragmentFile));
                IndexOutputs(project);
            }

            var modules = new List<ModuleDefinition>();
            foreach (var project in ordered.Where(project => selected.Contains(project.Definition.Name)))
            {
                var operations = new List<JObject>();
                foreach (var original in project.Fragment.Operations)
                {
                    var operation = (JObject)original.DeepClone();
                    string name = RequiredString(operation, "name");
                    NormalizeOperation(project, operation);
                    operation["name"] = QualifiedName(project, name);
                    project.Completed.Add(name);
                    operations.Add(operation);
                }

                if (project.Fragment.Exports != null)
                {
                    var exports = (JObject)project.Fragment.Exports.DeepClone();
                    exports.ToObject<Exports>(m_serializer);
                    var files = (JArray)(exports["files"] ?? new JArray());
                    var directories = (JArray)(exports["directories"] ?? new JArray());
                    foreach (JObject file in files)
                    {
                        var binding = ResolveFile(project, file);
                        string path = RequiredString(binding, "path");
                        AddExport(project, path, binding, isDirectory: false);
                    }
                    foreach (JObject directory in directories)
                    {
                        var binding = ResolveDirectory(project, directory);
                        string path = RequiredString(binding, "path");
                        AddExport(project, path, binding, isDirectory: true);
                    }
                    if (files.Count + directories.Count > 0)
                    {
                        operations.Add(new JObject
                        {
                            ["name"] = QualifiedName(project, "__directgraph_exports"),
                            ["kind"] = "export",
                            ["files"] = new JArray(project.Files.Values.Select(reference => reference.DeepClone())),
                            ["directories"] = new JArray(project.Directories.Values.Select(reference => reference.DeepClone())),
                        });
                    }
                }
                modules.Add(new ModuleDefinition { Name = project.Definition.Name, Operations = operations });
            }
            return new GraphDefinition { Modules = modules, IsProjectGraph = true };
        }

        private static void AddExport(Project project, string path, JObject binding, bool isDirectory)
        {
            if (project.Files.ContainsKey(path) || project.Directories.ContainsKey(path))
            {
                throw new InvalidDataException($"Project '{project.Definition.Name}' exports '{path}' more than once.");
            }
            (isDirectory ? project.Directories : project.Files).Add(path, binding);
        }

        private void IndexOutputs(Project project)
        {
            foreach (var operation in project.Fragment.Operations)
            {
                string name = RequiredString(operation, "name");
                string kind = RequiredString(operation, "kind");
                if (name == "__directgraph_exports" || !project.Operations.TryAdd(name, operation))
                {
                    throw new InvalidDataException($"Duplicate or reserved operation '{name}' in project '{project.Definition.Name}'.");
                }
                IEnumerable<string> files = kind switch
                {
                    "writeFile" or "copyFile" => new[] { RequiredString(operation, "destination") },
                    "process" => Strings(operation, "outputFiles").Concat(Objects(operation, "rewrittenFiles").Select(reference => RequiredString(reference, "path"))),
                    _ => Array.Empty<string>(),
                };
                project.ProducedFiles[name] = new HashSet<string>(files.Select(path => Absolute(project, path)), s_pathComparer);
                foreach (string path in project.ProducedFiles[name])
                {
                    if (m_fileOwners.TryGetValue(path, out string owner) && owner != project.Definition.Name)
                    {
                        throw new InvalidDataException($"File '{path}' has producers in projects '{owner}' and '{project.Definition.Name}'.");
                    }
                    m_fileOwners[path] = project.Definition.Name;
                }
                IEnumerable<string> directories = kind switch
                {
                    "process" => Strings(operation, "producesDirectories"),
                    "sealDirectory" or "composeSharedOpaque" => new[] { RequiredString(operation, "directoryRoot") },
                    _ => Array.Empty<string>(),
                };
                project.ProducedDirectories[name] = directories.Select(path => Absolute(project, path)).ToArray();
                if (kind is "process" or "composeSharedOpaque")
                {
                    m_dynamicDirectories.AddRange(project.ProducedDirectories[name].Select(root => (root, project.Definition.Name)));
                    m_dynamicProducers.Add(QualifiedName(project, name));
                }
            }
        }

        private void NormalizeOperation(Project project, JObject operation)
        {
            string kind = RequiredString(operation, "kind");
            void pathField(string field)
            {
                if (operation[field] != null)
                {
                    operation[field] = Absolute(project, RequiredString(operation, field));
                }
            }
            void fileReferences(string field)
            {
                operation[field] = new JArray(Objects(operation, field).Select(reference => ResolveFile(project, reference)));
            }

            switch (kind)
            {
                case "writeFile":
                    operation.ToObject<WriteFileOperationDefinition>(m_serializer);
                    pathField("destination");
                    break;
                case "copyFile":
                    operation.ToObject<CopyFileOperationDefinition>(m_serializer);
                    pathField("destination");
                    operation["source"] = ResolveFile(project, (JObject)operation["source"]);
                    break;
                case "process":
                    ExpandSets(project, operation, "inputSets", "inputFiles");
                    ExpandSets(project, operation, "orderOnlySets", "orderOnlyInputs");
                    operation.ToObject<ProcessOperationDefinition>(m_serializer);
                    if (operation["executableFile"] != null)
                    {
                        if (operation["executable"] != null)
                        {
                            throw new InvalidDataException("A process must specify either executable or executableFile, not both.");
                        }
                        operation["executableFile"] = ResolveFile(project, (JObject)operation["executableFile"]);
                    }
                    else
                    {
                        pathField("executable");
                        string executable = RequiredString(operation, "executable");
                        if (m_fileOwners.ContainsKey(executable))
                        {
                            operation["executableFile"] = ResolveFile(project, new JObject { ["sourcePath"] = executable });
                            operation.Remove("executable");
                        }
                    }
                    pathField("workingDirectory");
                    foreach (string field in new[] { "outputFiles", "producesDirectories" })
                    {
                        operation[field] = new JArray(Strings(operation, field).Select(path => Absolute(project, path)));
                    }
                    fileReferences("rewrittenFiles");
                    fileReferences("orderOnlyInputs");
                    var inputDirectories = new JArray(Objects(operation, "inputDirectories").Select(reference => ResolveDirectory(project, reference)));
                    var inputFiles = new JArray();
                    foreach (var reference in Objects(operation, "inputFiles"))
                    {
                        if (reference["sourcePath"] != null && reference["producedBy"] == null
                            && reference["dependency"] == null && reference["path"] == null)
                        {
                            string path = Absolute(project, RequiredString(reference, "sourcePath"));
                            if (!m_fileOwners.ContainsKey(path))
                            {
                                var directory = VisibleDirectory(project, path);
                                if (directory != null)
                                {
                                    inputDirectories.Add(directory.DeepClone());
                                    continue;
                                }
                            }
                        }
                        inputFiles.Add(ResolveFile(project, reference));
                    }
                    operation["inputFiles"] = inputFiles;
                    operation["inputDirectories"] = inputDirectories;
                    break;
                case "sealDirectory":
                    operation.ToObject<SealDirectoryOperationDefinition>(m_serializer);
                    pathField("directoryRoot");
                    fileReferences("contents");
                    if ((string)operation["sealKind"] is null or "partial" or "full")
                    {
                        m_directoryMembers[QualifiedName(project, RequiredString(operation, "name"))] = Objects(operation, "contents")
                            .ToDictionary(reference => (string)(reference["path"] ?? reference["sourcePath"]), reference => reference, s_pathComparer);
                    }
                    break;
                case "composeSharedOpaque":
                    operation.ToObject<ComposeSharedOpaqueOperationDefinition>(m_serializer);
                    pathField("directoryRoot");
                    operation["contents"] = new JArray(Objects(operation, "contents").Select(reference => ResolveDirectory(project, reference)));
                    break;
                default:
                    throw new InvalidDataException($"Unsupported fragment operation kind '{kind}'. Exports belong in the fragment's exports object.");
            }
        }

        private static void ExpandSets(Project project, JObject operation, string setsField, string filesField)
        {
            var files = new JArray(Objects(operation, filesField).Select(reference => reference.DeepClone()));
            foreach (string name in Strings(operation, setsField))
            {
                if (project.Fragment.DependencySets == null || !project.Fragment.DependencySets.TryGetValue(name, out var members))
                {
                    throw new InvalidDataException($"Unknown dependency set '{name}' in project '{project.Definition.Name}'.");
                }
                foreach (var member in members)
                {
                    files.Add(member.DeepClone());
                }
            }
            operation.Remove(setsField);
            operation[filesField] = files;
        }

        private JObject ResolveFile(Project project, JObject reference)
        {
            reference.ToObject<FileReferenceDefinition>(m_serializer);
            if (reference["dependency"] != null)
            {
                if (reference["sourcePath"] != null || reference["producedBy"] != null)
                {
                    throw new InvalidDataException("A dependency reference cannot also name a source or local producer.");
                }
                return DependencyExport(project, RequiredString(reference, "dependency"), Absolute(project, RequiredString(reference, "path")), isDirectory: false);
            }
            if (reference["sourcePath"] != null)
            {
                if (reference["producedBy"] != null || reference["path"] != null)
                {
                    throw new InvalidDataException("A source reference cannot also name a producer.");
                }
                string path = Absolute(project, RequiredString(reference, "sourcePath"));
                if (m_fileOwners.TryGetValue(path, out string owner))
                {
                    if (owner == project.Definition.Name)
                    {
                        throw new InvalidDataException($"Generated input '{path}' must name its local producer.");
                    }
                    return DependencyExport(project, owner, path, isDirectory: false);
                }
                if (m_dynamicDirectories.Any(directory => IsWithin(path, directory.Root)))
                {
                    throw new InvalidDataException($"Generated directory member '{path}' requires an exported input directory.");
                }
                return new JObject { ["sourcePath"] = path };
            }
            string producer = RequiredString(reference, "producedBy");
            string producedPath = Absolute(project, RequiredString(reference, "path"));
            if (!project.Completed.Contains(producer) || !project.ProducedFiles[producer].Contains(producedPath))
            {
                throw new InvalidDataException($"File '{producedPath}' is not an output of completed local operation '{producer}'.");
            }
            return new JObject { ["producedBy"] = QualifiedName(project, producer), ["path"] = producedPath };
        }

        private JObject ResolveDirectory(Project project, JObject reference)
        {
            reference.ToObject<DirectoryReferenceDefinition>(m_serializer);
            if (reference["dependency"] != null)
            {
                if (reference["producedBy"] != null)
                {
                    throw new InvalidDataException("A dependency directory reference cannot also name a local producer.");
                }
                return DependencyExport(project, RequiredString(reference, "dependency"), Absolute(project, RequiredString(reference, "path")), isDirectory: true);
            }
            string producer = RequiredString(reference, "producedBy");
            if (!project.Completed.Contains(producer))
            {
                throw new InvalidDataException($"Directory producer '{producer}' has not completed construction.");
            }
            var roots = project.ProducedDirectories[producer];
            string path = reference["path"] == null && roots.Length == 1 ? roots[0] : Absolute(project, RequiredString(reference, "path"));
            if (!roots.Contains(path, s_pathComparer))
            {
                throw new InvalidDataException($"Operation '{producer}' does not produce directory '{path}'.");
            }
            return new JObject { ["producedBy"] = QualifiedName(project, producer), ["path"] = path };
        }

        private JObject DependencyExport(Project project, string dependency, string path, bool isDirectory)
        {
            if (!project.Definition.Dependencies.Contains(dependency, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Project '{project.Definition.Name}' cannot import '{path}' from nondependency '{dependency}'.");
            }
            var exports = isDirectory ? m_projects[dependency].Directories : m_projects[dependency].Files;
            if (!exports.TryGetValue(path, out var reference))
            {
                if (!isDirectory)
                {
                    foreach (var directory in m_projects[dependency].Directories.Values)
                    {
                        if (m_directoryMembers.TryGetValue(RequiredString(directory, "producedBy"), out var members) && members.TryGetValue(path, out reference))
                        {
                            return (JObject)reference.DeepClone();
                        }
                    }
                }
                throw new InvalidDataException($"Dependency '{dependency}' does not export {(isDirectory ? "directory" : "file")} '{path}'.");
            }
            return (JObject)reference.DeepClone();
        }

        private JObject VisibleDirectory(Project project, string path)
        {
            var matches = project.Definition.Dependencies
                .SelectMany(dependency => m_projects[dependency].Directories)
                .Where(export => IsWithin(path, export.Key)
                    && (m_dynamicProducers.Contains(RequiredString(export.Value, "producedBy"))
                        || (m_directoryMembers.TryGetValue(RequiredString(export.Value, "producedBy"), out var members) && members.ContainsKey(path))))
                .Select(export => export.Value)
                .ToArray();
            if (matches.Length > 1)
            {
                throw new InvalidDataException($"Generated input '{path}' is covered by multiple dependency directory exports; specify an input directory explicitly.");
            }
            return matches.SingleOrDefault();
        }

        private static bool IsWithin(string path, string root) =>
            s_pathComparer.Equals(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        private static string Absolute(Project project, string path) => Path.GetFullPath(path, project.Root);
        private static string QualifiedName(Project project, string name) => project.Definition.Name.Length + ":" + project.Definition.Name + ":" + name;
        private static IEnumerable<string> Strings(JObject json, string field) => json[field]?.ToObject<string[]>() ?? Array.Empty<string>();
        private static IEnumerable<JObject> Objects(JObject json, string field) => json[field]?.ToObject<JObject[]>() ?? Array.Empty<JObject>();

        private static string RequiredString(JObject json, string field)
        {
            if (json[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)json[field]))
            {
                throw new InvalidDataException($"'{field}' must be a nonempty string.");
            }
            return (string)json[field];
        }

        private static JObject ReadObject(string file)
        {
            using var reader = File.OpenText(file);
            using var jsonReader = new JsonTextReader(reader);
            var result = JObject.Load(jsonReader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (jsonReader.Read())
            {
                throw new InvalidDataException($"Unexpected trailing JSON in '{file}'.");
            }
            return result;
        }

        private static void CheckVersion(JObject json)
        {
            if (json["formatVersion"]?.Type != JTokenType.Integer || (int)json["formatVersion"] != 1)
            {
                throw new InvalidDataException("A project manifest or fragment must declare formatVersion 1.");
            }
        }

        private sealed class Project
        {
            public Project(ProjectDefinition definition) { Definition = definition; }
            public ProjectDefinition Definition { get; }
            public Fragment Fragment { get; set; }
            public string Root { get; set; }
            public Dictionary<string, JObject> Operations { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, HashSet<string>> ProducedFiles { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, string[]> ProducedDirectories { get; } = new(StringComparer.Ordinal);
            public HashSet<string> Completed { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, JObject> Files { get; } = new(s_pathComparer);
            public Dictionary<string, JObject> Directories { get; } = new(s_pathComparer);
        }

        private sealed class Manifest
        {
            public int FormatVersion { get; set; }
            [JsonProperty(Required = Required.Always)]
            public IReadOnlyList<ProjectDefinition> Projects { get; set; }
            public IReadOnlyList<string> Roots { get; set; }
        }

        private sealed class ProjectDefinition
        {
            [JsonProperty(Required = Required.Always)]
            public string Name { get; set; }
            [JsonProperty(Required = Required.Always)]
            public string Fragment { get; set; }
            [JsonProperty(Required = Required.Always)]
            public IReadOnlyList<string> Dependencies { get; set; }
            public string Root { get; set; }
        }

        private sealed class Fragment
        {
            public int FormatVersion { get; set; }
            public string Root { get; set; }
            [JsonProperty(Required = Required.Always)]
            public IReadOnlyList<JObject> Operations { get; set; }
            public JObject Exports { get; set; }
            public IReadOnlyDictionary<string, IReadOnlyList<JObject>> DependencySets { get; set; }
        }

        private sealed class Exports
        {
            public IReadOnlyList<FileReferenceDefinition> Files { get; set; }
            public IReadOnlyList<DirectoryReferenceDefinition> Directories { get; set; }
        }
    }
}
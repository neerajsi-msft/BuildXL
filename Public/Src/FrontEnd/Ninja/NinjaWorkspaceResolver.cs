// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.FrontEnd.Sdk;
using BuildXL.FrontEnd.Utilities;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.FrontEnd.Workspaces.Core;
using BuildXL.Native.IO;
using BuildXL.Processes;
using BuildXL.ProcessPipExecutor;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using Newtonsoft.Json;
using TypeScript.Net.Types;
using static BuildXL.Utilities.Core.FormattableStringEx;
using System.Linq;

namespace BuildXL.FrontEnd.Ninja
{
    /// <summary>
    /// Workspace resolver using a custom JSON generator from ninja specs
    /// </summary>
    public sealed class NinjaWorkspaceResolver : ProjectGraphWorkspaceResolverBase<NinjaGraphWithModuleDefinition, INinjaResolverSettings>
    {
        internal const string NinjaResolverName = "Ninja";
        private AbsolutePath m_pathToTool;
        internal AbsolutePath ProjectRoot;
        internal AbsolutePath SpecFile;
        internal IReadOnlyList<NinjaProject> Projects { get; private set; }

 
        /// <summary>
        /// The path for the JSON graph associated with this resolver.
        /// This file exists only after the graph is successfully built.
        /// </summary>
        /// <remarks>
        /// Marked as internal - we may want to use this in the FrontEnd resolver for logging purposes
        /// </remarks>
        internal Lazy<AbsolutePath> SerializedGraphPath;

        /// <summary>
        /// Keep in sync with the BuildXL deployment spec that places the tool (\Public\Src\Deployment\buildXL.dsc)
        /// </summary>
        private readonly string m_ninjaGraphBuilderRelativePath = OperatingSystemHelper.IsWindowsOS ? @"tools\NinjaGraphBuilder\NinjaGraphBuilder.exe" : @"tools/NinjaGraphBuilder/NinjaGraphBuilder";

        /// <inheritdoc/>
        public NinjaWorkspaceResolver()
        {
            Name = "Ninja";
            SerializedGraphPath = new Lazy<AbsolutePath>(GetToolOutputPath);
        }

        /// <inheritdoc/>
        public override string Kind => KnownResolverKind.NinjaResolverKind;

        /// <inheritdoc />
        /// <summary>
        /// Initializes the workspace resolver
        /// </summary>
        public override bool TryInitialize(
            FrontEndHost host,
            FrontEndContext context,
            IConfiguration configuration,
            IResolverSettings resolverSettings)
        {
            if (!base.TryInitialize(host, context, configuration, resolverSettings))
            {
                return false;
            }

            var relativePathToGraphConstructionTool = RelativePath.Create(context.StringTable, m_ninjaGraphBuilderRelativePath);
            m_pathToTool = configuration.Layout.BuildEngineDirectory.Combine(Context.PathTable, relativePathToGraphConstructionTool);


            var projects = new List<NinjaProject>();
            if (ResolverSettings.Projects != null)
            {
                if (ResolverSettings.Projects.Count == 0 ||
                    ResolverSettings.Root.IsValid || ResolverSettings.SpecFile.IsValid ||
                    ResolverSettings.Targets != null || ResolverSettings.Environment != null)
                {
                    return InvalidSettings("projects must be nonempty and cannot be combined with root, specFile, targets or environment at resolver level.");
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var project in ResolverSettings.Projects)
                {
                    if (project == null || string.IsNullOrWhiteSpace(project.Name) ||
                        !names.Add(project.Name) || project.Environment == null ||
                        (!project.Root.IsValid && !project.SpecFile.IsValid))
                    {
                        return InvalidSettings("Each Ninja project needs a unique nonempty name, an explicit environment map, and a root or specFile.");
                    }

                    projects.Add(new NinjaProject(project, Context.PathTable));
                }
            }
            else
            {
                if (!ResolverSettings.Root.IsValid && !ResolverSettings.SpecFile.IsValid)
                {
                    return InvalidSettings("Either a project root or a spec file location (or both) must be specified.");
                }

                projects.Add(new NinjaProject(new global::BuildXL.Utilities.Configuration.Mutable.NinjaProjectSettings
                {
                    Name = ResolverSettings.ModuleName,
                    Root = ResolverSettings.Root,
                    SpecFile = ResolverSettings.SpecFile,
                    Targets = ResolverSettings.Targets,
                    Environment = ResolverSettings.Environment,
                }, Context.PathTable));
            }

            foreach (var project in projects)
            {
                string path;
                if (!Directory.Exists(path = project.Root.ToString(Context.PathTable)))
                {
                    Tracing.Logger.Log.ProjectRootDirectoryDoesNotExist(Context.LoggingContext, ResolverSettings.Location(Context.PathTable), path);
                    return false;
                }

                if (!File.Exists(path = project.SpecFile.ToString(Context.PathTable)))
                {
                    Tracing.Logger.Log.NinjaSpecFileDoesNotExist(Context.LoggingContext, ResolverSettings.Location(Context.PathTable), path);
                    return false;
                }
            }

            Projects = projects;
            ProjectRoot = projects[0].Root;
            SpecFile = projects[0].SpecFile;
            return true;
        }

        private bool InvalidSettings(string reason)
        {
            Tracing.Logger.Log.InvalidResolverSettings(Context.LoggingContext, ResolverSettings.Location(Context.PathTable), reason);
            return false;
        }

        /// <inheritdoc />
        protected override async Task<Possible<NinjaGraphWithModuleDefinition>> TryComputeBuildGraphAsync()
        {
            var nodes = new List<NinjaNode>();
            var targets = new List<NinjaTarget>();
            foreach (var project in Projects)
            {
                Possible<NinjaGraphResult> maybeGraph = await ComputeBuildGraphAsync(project);
                if (!maybeGraph.Succeeded)
                {
                    return maybeGraph.Failure;
                }

                if (!maybeGraph.Result.Succeeded)
                {
                    InvalidSettings($"Project '{project.Name}': {maybeGraph.Result.FailureReason}");
                    return new NinjaGraphConstructionFailure(ResolverSettings.ModuleName, project.Root.ToString(Context.PathTable));
                }

                project.Nodes = maybeGraph.Result.Graph.Nodes;
                nodes.AddRange(project.Nodes);
                targets.AddRange(maybeGraph.Result.Graph.Targets);
            }

            var specFileConfig = SpecFile.ChangeExtension(Context.PathTable, PathAtom.Create(Context.StringTable, ".ninja.dsc")); // It needs to be a .dsc for the parsing to work

            var moduleDescriptor = ModuleDescriptor.CreateWithUniqueId(Context.StringTable, ResolverSettings.ModuleName, this);
            var moduleDefinition = ModuleDefinition.CreateModuleDefinitionWithImplicitReferences(
                moduleDescriptor,
                ProjectRoot,
                ResolverSettings.File,
                new List<AbsolutePath>() { specFileConfig } ,
                allowedModuleDependencies: null, // no module policies
                cyclicalFriendModules: null, // no allowlist of cycles
                mounts: null,
                scrubDirectories: null);

            return new NinjaGraphWithModuleDefinition(new NinjaGraph { Nodes = nodes, Targets = targets }, moduleDefinition);
        }
        
        private async Task<Possible<NinjaGraphResult>> ComputeBuildGraphAsync(NinjaProject project)
        {
            AbsolutePath outputFile = ResolverSettings.Projects == null ? SerializedGraphPath.Value : GetToolOutputPath();

            SandboxedProcessResult result = await RunNinjaGraphBuilderAsync(outputFile, project);

            string standardError = result.StandardError.ReadValueAsync().GetAwaiter().GetResult();
            if (result.ExitCode != 0)
            {
                if (!Context.CancellationToken.IsCancellationRequested)
                {

                    Tracing.Logger.Log.GraphConstructionInternalError(
                        Context.LoggingContext,
                        ResolverSettings.Location(Context.PathTable),
                        standardError);
                }

                return new NinjaGraphConstructionFailure(ResolverSettings.ModuleName, ProjectRoot.ToString(Context.PathTable));
            }

            // If the tool exited gracefully, but standard error is not empty, that is interpreted as a warning
            if (!string.IsNullOrEmpty(standardError))
            {
                Tracing.Logger.Log.GraphConstructionFinishedSuccessfullyButWithWarnings(
                    Context.LoggingContext,
                    ResolverSettings.Location(Context.PathTable),
                    standardError);
            }
            
            var accesses = result.AllUnexpectedFileAccesses.Union(result.ExplicitlyReportedFileAccesses);
            if (ResolverSettings.Projects == null)
            {
                TrackFilesAndEnvironment(accesses, outputFile.GetParent(Context.PathTable));
            }
            else
            {
                // Grouped projects have explicit configuration environments, not ambient host environments.
                FrontEndUtilities.TrackToolFileAccessesAndEnvironment(
                    Host.Engine, Context, frontEndName: Name, resolverName: Name,
                    environmentVariables: CollectionUtilities.EmptyArray<string>(),
                    accesses, outputFile.GetParent(Context.PathTable));
            }
            var serializer = JsonSerializer.Create(GraphSerializationSettings.Settings);
            
            // Add custom deserializer for converting string arrays to AbsolutePath ReadOnlySets
            serializer.Converters.Add(new RootAwareAbsolutePathConverter(Context.PathTable, project.Root));
            serializer.Converters.Add(new ToReadOnlySetJsonConverter<AbsolutePath>());
            
            var outputFileString = outputFile.ToString(Context.PathTable);
            Tracing.Logger.Log.LeftGraphToolOutputAt(Context.LoggingContext, ResolverSettings.Location(Context.PathTable), outputFileString);

            NinjaGraphResult projectGraphWithPredictionResult;

            try
            {
                using (var sr = new StreamReader(outputFileString))
                using (var reader = new JsonTextReader(sr))
                {
                    projectGraphWithPredictionResult = serializer.Deserialize<NinjaGraphResult>(reader);
                }
            }
            catch (Exception ex)
            {
                Tracing.Logger.Log.GraphConstructionDeserializationError(
                    Context.LoggingContext,
                    ResolverSettings.Location(Context.PathTable),
                    ex.ToString());
                return new NinjaGraphConstructionFailure(ResolverSettings.ModuleName, ProjectRoot.ToString(Context.PathTable));
            }

            return projectGraphWithPredictionResult;
        }

        private Task<SandboxedProcessResult> RunNinjaGraphBuilderAsync(AbsolutePath outputFile, NinjaProject project)
        {
            AbsolutePath outputDirectory = outputFile.GetParent(Context.PathTable);
            FileUtilities.CreateDirectory(outputDirectory.ToString(Context.PathTable)); // Ensure it exists

            AbsolutePath argumentsFile = outputDirectory.Combine(Context.PathTable, Guid.NewGuid().ToString());
            SerializeToolArguments(outputFile, argumentsFile, project);

            // After running the tool we'd like to remove some files 
            void cleanUpOnResult()
            {
                try
                {
                    var shouldKeepArgs = ResolverSettings.KeepProjectGraphFile ?? false;
                    if (!shouldKeepArgs)
                    {
                        FileUtilities.DeleteFile(argumentsFile.ToString(Context.PathTable));
                    }
                }
                catch (BuildXLException e)
                {
                    Tracing.Logger.Log.CouldNotDeleteToolArgumentsFile(
                        Context.LoggingContext,
                        ResolverSettings.Location(Context.PathTable),
                        argumentsFile.ToString(Context.PathTable),
                        e.Message);
                }
            }

            
            return FrontEndUtilities.RunSandboxedToolAsync(
                Context,
                m_pathToTool.ToString(Context.PathTable),
                buildStorageDirectory: outputDirectory.ToString(Context.PathTable),
                fileAccessManifest: FrontEndUtilities.GenerateToolFileAccessManifest(Context, m_pathToTool.GetParent(Context.PathTable)),
                arguments: I($@"""{argumentsFile.ToString(Context.PathTable)}"""),
                workingDirectory: project.Root.ToString(Context.PathTable),
                description: "Ninja graph builder",
                RetrieveBuildParameters(project),
                onResult: cleanUpOnResult,
                useEBPFLinuxSandbox: Configuration.Sandbox.EnableEBPFLinuxSandbox,
                ebpfRingBufferSizeMultiplier: Configuration.Sandbox.EBPFRingBufferSizeMultiplier);
        }

        private BuildParameters.IBuildParameters RetrieveBuildParameters(NinjaProject project)
        {
            var environment = project.Environment.Concat(project.PassthroughVariables.Select(
                name => new KeyValuePair<string, string>(name, Environment.GetEnvironmentVariable(name))));
            return BuildParameters.GetFactory().PopulateFromEnvironment()
                .Select(FrontEndUtilities.GraphConstructionEnvironmentVariableAllowlist)
                .Override(BuildParameters.GetFactory().PopulateFromDictionary(environment).ToDictionary());
        }

        private void SerializeToolArguments(AbsolutePath outputFile, AbsolutePath argumentsFile, NinjaProject project)
        {
            var arguments = new NinjaGraphToolArguments()
                            {
                                BuildFileName = project.SpecFile.ToString(Context.PathTable),
                                ProjectRoot = project.Root.ToString(Context.PathTable),
                                OutputFile = outputFile.ToString(Context.PathTable),
                                Targets = project.Targets?.ToArray() ?? CollectionUtilities.EmptyArray<string>()
                            };


            var serializer = JsonSerializer.Create(GraphSerializationSettings.Settings);
            using (var sw = new StreamWriter(argumentsFile.ToString(Context.PathTable)))
            using (var writer = new JsonTextWriter(sw))
            {
                serializer.Serialize(writer, arguments);
            }
        }

        /// <summary>
        /// Get the output path for the JSON graph associated with this resolver
        /// </summary>
        private AbsolutePath GetToolOutputPath()
        {
            AbsolutePath outputDirectory = Host.GetFolderForFrontEnd(Name);
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd-THH-mm-ss.SSS-Z");
            var uniqueName = $"ninja_graph_{now}_{Guid.NewGuid():N}.json";
            return outputDirectory.Combine(Context.PathTable, uniqueName);
        }

        /// <inheritdoc />
        protected override SourceFile DoCreateSourceFile(AbsolutePath path) =>
            // This is the interop point to advertise values to other DScript specs
            // For now we just return an empty SourceFile
            SourceFile.Create(path.ToString(Context.PathTable));

        private sealed class NinjaGraphBuildStorage : ISandboxedProcessFileStorage
        {
            private readonly string m_directory;

            /// <nodoc />
            public NinjaGraphBuildStorage(string directory) => m_directory = directory;

            /// <inheritdoc />
            public string GetFileName(SandboxedProcessFile file) => Path.Combine(m_directory, file.DefaultFileName());
        }
    }
}

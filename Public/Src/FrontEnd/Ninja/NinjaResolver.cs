// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.FrontEnd.Sdk;
using BuildXL.FrontEnd.Sdk.Evaluation;
using BuildXL.FrontEnd.Sdk.Workspaces;
using BuildXL.FrontEnd.Workspaces.Core;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Configuration;

namespace BuildXL.FrontEnd.Ninja
{
    /// <summary>
    /// Resolver for static graph Ninja based builds.
    /// </summary>
    public class NinjaResolver : IResolver
    {
        private readonly string m_frontEndName;
        private readonly FrontEndContext m_context;
        private readonly FrontEndHost m_host;

        private NinjaWorkspaceResolver m_ninjaWorkspaceResolver;
        private INinjaResolverSettings m_ninjaResolverSettings;
        private ModuleDefinition ModuleDef => m_ninjaWorkspaceResolver.ComputedProjectGraph.Result.ModuleDefinition;

        /// <nodoc />
        public string Name { get; private set; }

        /// <nodoc/>
        public NinjaResolver(
            FrontEndHost host,
            FrontEndContext context,
            string frontEndName)
        {
            Contract.Requires(!string.IsNullOrEmpty(frontEndName));
            m_frontEndName = frontEndName;
            m_host = host;
            m_context = context;
        }

        /// <inheritdoc/>
        public Task<bool> InitResolverAsync(IResolverSettings resolverSettings, object workspaceResolver)
        {
            Contract.Requires(resolverSettings != null);

            Name = resolverSettings.Name;
            m_ninjaResolverSettings = resolverSettings as INinjaResolverSettings; 
            m_ninjaWorkspaceResolver = workspaceResolver as NinjaWorkspaceResolver;
            
            // TODO: Failure cases, logging
            return Task.FromResult(true);
        }


        /// <inheritdoc/>
        public void LogStatistics()
        {
        }

        /// <inheritdoc/>
        public Task<bool?> TryConvertModuleToEvaluationAsync(IModuleRegistry moduleRegistry, ParsedModule module, IWorkspace workspace) =>
            // No conversion needed.
            Task.FromResult<bool?>(true);

        /// <inheritdoc/>
        public async Task<bool?> TryEvaluateModuleAsync(IEvaluationScheduler scheduler, ModuleDefinition iModule, QualifierId qualifierId) =>
            // Note: we are effectively evaluating iModule
            // Using await to suppress compiler warnings
            // TODO: Async?
            !iModule.Equals(ModuleDef) ? null : await Task.FromResult(TryEvaluate(ModuleDef, qualifierId)); 

        private bool? TryEvaluate(ModuleDefinition module, QualifierId qualifierId)    // TODO: Async?
        {
            if (m_ninjaResolverSettings.Projects != null)
            {
                return TryEvaluateProjects(module, qualifierId);
            }
            
            NinjaGraphWithModuleDefinition result = m_ninjaWorkspaceResolver.ComputedProjectGraph.Result;
            
            // TODO: Actual filtering. For now [first development only] we schedule all pips because we are dealing with a single spec 'all'
            IReadOnlyCollection<NinjaNode> filteredNodes = result.Graph.Nodes;

            var constructor = new NinjaPipConstructor(
                m_context,
                m_host,
                m_frontEndName,
                ModuleDef,
                qualifierId,
                m_ninjaWorkspaceResolver.ProjectRoot,
                m_ninjaWorkspaceResolver.SpecFile,
                new()
                {
                    UserDefinedEnvironment = m_ninjaWorkspaceResolver.TrackedEnvironmentVariables,
                    UserDefinedPassthroughVariables = m_ninjaWorkspaceResolver.UserDefinedPassthroughEnvironmentVariables,
                    UntrackingSettings = m_ninjaResolverSettings,
                    AdditionalOutputDirectories = m_ninjaResolverSettings.AdditionalOutputDirectories,
                    AllowWritableSourceDirectory = m_ninjaResolverSettings.AllowWritableSourceDirectory,
                }, sourceDirectories: m_ninjaResolverSettings.SourceDirectories);

            return filteredNodes.All(node => constructor.TrySchedulePip(node, out _));
        }

        private bool TryEvaluateProjects(ModuleDefinition module, QualifierId qualifierId)
        {
            var projects = m_ninjaWorkspaceResolver.Projects;
            if (!NinjaGraphStitcher.TryStitch(projects, m_context.PathTable, out var graph, out var error))
            {
                Tracing.Logger.Log.InvalidResolverSettings(m_context.LoggingContext,
                    m_ninjaResolverSettings.Location(m_context.PathTable), error);
                return false;
            }

            if (projects.Any(project => project.DependencyNames != null || project.FileExports != null || project.GeneratedInputs.Count != 0))
            {
                return TryEvaluateProjectGraph(projects, graph, module, qualifierId);
            }

            var sharedState = new NinjaPipConstructor.SharedState { DirectoryMembers = graph.DirectoryMembers };
            var constructors = new Dictionary<NinjaProject, NinjaPipConstructor>();
            foreach (var project in projects)
            {
                constructors.Add(project, new NinjaPipConstructor(
                    m_context, m_host, m_frontEndName, module, qualifierId, project.Root, project.SpecFile,
                    new NinjaPipConstructionSettings
                    {
                        UserDefinedEnvironment = project.Environment,
                        UserDefinedPassthroughVariables = project.PassthroughVariables,
                        UntrackingSettings = m_ninjaResolverSettings,
                        AdditionalOutputDirectories = m_ninjaResolverSettings.AdditionalOutputDirectories,
                        AllowWritableSourceDirectory = m_ninjaResolverSettings.AllowWritableSourceDirectory,
                    }, sharedState, project.Name, graph.DirectoryImports[project], project.DirectoryOutputs, project.SourceDirectories));
            }

            foreach (var (node, project) in graph.Nodes)
            {
                if (!constructors[project].TrySchedulePip(node, out _))
                {
                    Tracing.Logger.Log.InvalidResolverSettings(m_context.LoggingContext,
                        m_ninjaResolverSettings.Location(m_context.PathTable),
                        $"Failed to schedule stitched Ninja rule '{node.Rule}'. See the preceding pip diagnostics.");
                    return false;
                }
            }

            return true;
        }

        private bool TryEvaluateProjectGraph(
            IReadOnlyList<NinjaProject> projects, NinjaGraphStitcher graph, ModuleDefinition module, QualifierId qualifierId)
        {
            var byName = projects.ToDictionary(project => project.Name, System.StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects)
            {
                var dependencies = new HashSet<NinjaProject>();
                foreach (var name in project.DependencyNames ?? System.Array.Empty<string>())
                {
                    if (string.IsNullOrEmpty(name) || !byName.TryGetValue(name, out var dependency) || !dependencies.Add(dependency))
                    {
                        Tracing.Logger.Log.InvalidResolverSettings(m_context.LoggingContext,
                            m_ninjaResolverSettings.Location(m_context.PathTable),
                            $"Project '{project.Name}' declares an unknown or duplicate dependency '{name}'.");
                        return false;
                    }
                }

                project.Dependencies = dependencies;
            }

            var constructor = new NinjaProjectGraphConstructor(graph, m_context.PathTable,
                (project, state, qualifier) => new NinjaPipConstructor(
                    m_context, m_host, m_frontEndName, module, qualifier, project.Root, project.SpecFile,
                    new NinjaPipConstructionSettings
                    {
                        UserDefinedEnvironment = project.Environment,
                        UserDefinedPassthroughVariables = project.PassthroughVariables,
                        UntrackingSettings = m_ninjaResolverSettings,
                        AdditionalOutputDirectories = m_ninjaResolverSettings.AdditionalOutputDirectories,
                        AllowWritableSourceDirectory = m_ninjaResolverSettings.AllowWritableSourceDirectory,
                    }, state, project.Name, graph.DirectoryImports[project], project.DirectoryOutputs, project.SourceDirectories));
            var result = new ProjectGraphToGraphConstructor<NinjaProject>(constructor)
                .TrySchedule(projects.ToHashSet(), qualifierId);
            if (!result.Succeeded)
            {
                Tracing.Logger.Log.InvalidResolverSettings(m_context.LoggingContext,
                    m_ninjaResolverSettings.Location(m_context.PathTable), result.Failure.Describe());
            }

            return result.Succeeded;
        }

        /// <inheritdoc/>
        public void NotifyEvaluationFinished()
        {
            // Nothing to do.
        }
       
    }
}

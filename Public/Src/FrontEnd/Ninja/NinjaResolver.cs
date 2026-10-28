// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Linq;
using System.Threading.Tasks;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.FrontEnd.Sdk;
using BuildXL.FrontEnd.Sdk.Evaluation;
using BuildXL.FrontEnd.Sdk.Workspaces;
using BuildXL.FrontEnd.Workspaces.Core;
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

            var graphConstructor = new NinjaPipGraphBuilder(
                m_context,
                m_host,
                ModuleDef,
                m_ninjaWorkspaceResolver.ProjectRoot,
                m_ninjaWorkspaceResolver.SpecFile,
                qualifierId,
                m_frontEndName,
                new()
                {
                    UserDefinedEnvironment = m_ninjaWorkspaceResolver.TrackedEnvironmentVariables,
                    UserDefinedPassthroughVariables = m_ninjaWorkspaceResolver.UserDefinedPassthroughEnvironmentVariables,
                    UntrackingSettings = m_ninjaResolverSettings,
                    AdditionalOutputDirectories = m_ninjaResolverSettings.AdditionalOutputDirectories,
                    AllowWritableSourceDirectory = m_ninjaResolverSettings.AllowWritableSourceDirectory,
                }, m_ninjaResolverSettings.SourceDirectories);

            return graphConstructor.TrySchedulePips(filteredNodes, qualifierId);
        }

        private bool TryEvaluateProjects(ModuleDefinition module, QualifierId qualifierId)
        {
            var projects = m_ninjaWorkspaceResolver.Projects;
            if (!NinjaGraphStitcher.TrySort(projects, m_context.PathTable, out var nodes, out var error))
            {
                Tracing.Logger.Log.InvalidResolverSettings(m_context.LoggingContext,
                    m_ninjaResolverSettings.Location(m_context.PathTable), error);
                return false;
            }

            var sharedState = new NinjaPipConstructor.SharedState();
            var projectNames = projects.ToDictionary(project => project.Name, project => project.Name, StringComparer.OrdinalIgnoreCase);
            var constructors = new Dictionary<NinjaNode, NinjaPipConstructor>();
            foreach (var project in projects)
            {
                foreach (var root in project.DirectoryExports)
                {
                    sharedState.DirectoryMembers.Add((project.Name, root), project.Nodes
                        .Where(node => node.Rule != "phony").SelectMany(node => node.Outputs)
                        .Where(path => path.IsWithin(m_context.PathTable, root)).Distinct().ToArray());
                }

                var constructor = new NinjaPipConstructor(
                    m_context, m_host, m_frontEndName, module, qualifierId, project.Root, project.SpecFile,
                    new NinjaPipConstructionSettings
                    {
                        UserDefinedEnvironment = project.Environment,
                        UserDefinedPassthroughVariables = project.PassthroughVariables,
                        UntrackingSettings = m_ninjaResolverSettings,
                        AdditionalOutputDirectories = m_ninjaResolverSettings.AdditionalOutputDirectories,
                        AllowWritableSourceDirectory = m_ninjaResolverSettings.AllowWritableSourceDirectory,
                    }, sharedState, project.Name, project.DirectoryImports.ToDictionary(
                        import => projectNames[import.Key], import => import.Value), project.DirectoryOutputs, project.SourceDirectories);
                foreach (var node in project.Nodes)
                {
                    constructors.Add(node, constructor);
                }
            }

            foreach (var node in nodes)
            {
                if (!constructors[node].TrySchedulePip(node, qualifierId, out _))
                {
                    Tracing.Logger.Log.InvalidResolverSettings(m_context.LoggingContext,
                        m_ninjaResolverSettings.Location(m_context.PathTable),
                        $"Failed to schedule stitched Ninja rule '{node.Rule}'. See the preceding pip diagnostics.");
                    return false;
                }
            }

            return true;
        }

        /// <inheritdoc/>
        public void NotifyEvaluationFinished()
        {
            // Nothing to do.
        }
       
    }
}

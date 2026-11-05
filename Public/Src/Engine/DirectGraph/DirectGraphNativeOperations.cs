// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Core;
using NativeSealKind = BuildXL.Pips.Operations.SealDirectoryKind;

namespace BuildXL.Engine.DirectGraph
{
    public delegate bool PipConstructionHelperResolver(string moduleName, string symbol, out PipConstructionHelper helper, out string error);
    public delegate bool TryRegisterExport(string moduleName, FileOrDirectoryArtifact artifact, out string error);

    /// <summary>An explicit native construction operation, independent of transport IDs.</summary>
    public abstract class DirectGraphOperation
    {
        public string Module { get; set; }
        public string Symbol { get; set; }
        public StringId Description { get; set; }
        public List<StringId> Tags { get; } = new();
    }

    public sealed class DirectGraphProcess : DirectGraphOperation
    {
        public FileArtifact Executable { get; set; }
        public AbsolutePath WorkingDirectory { get; set; }
        public PipData Arguments { get; set; }
        public StringId ToolDescription { get; set; }
        public Process.Options Options { get; set; } = Process.Options.RequireGlobalDependencies;
        public RewritePolicy? DoubleWritePolicy { get; set; }
        public RewritePolicy? SourceRewritePolicy { get; set; }
        public Process.AbsentPathProbeInUndeclaredOpaquesMode? AbsentPathProbeMode { get; set; }
        public List<FileArtifact> InputFiles { get; } = new();
        public List<DirectoryArtifact> InputDirectories { get; } = new();
        public List<DirectGraphFileOutput> OutputFiles { get; } = new();
        public List<DirectGraphDirectoryOutput> OutputDirectories { get; } = new();
        public List<DirectGraphOrderDependency> OrderOnlyInputs { get; } = new();
        public List<DirectGraphEnvironmentVariable> EnvironmentVariables { get; } = new();
        public List<DirectGraphSemaphore> Semaphores { get; } = new();
        public List<AbsolutePath> UntrackedPaths { get; } = new();
        public List<AbsolutePath> UntrackedScopes { get; } = new();
        public AbsolutePath TempDirectory { get; set; }
        public bool EnableTempDirectory { get; set; }
        public List<AbsolutePath> AdditionalTempDirectories { get; } = new();
        public List<AbsolutePath> OutputDirectoryExclusions { get; } = new();
        public List<AbsolutePath> AllowedUndeclaredSourceReadScopes { get; } = new();
        public List<AbsolutePath> AllowedUndeclaredSourceReadPaths { get; } = new();
        public List<StringId> AllowedSurvivingChildProcessNames { get; } = new();
        public List<DirectGraphBreakawayChildProcess> BreakawayChildProcesses { get; } = new();
        public List<int> SuccessExitCodes { get; } = new();
        public List<int> RetryExitCodes { get; } = new();
        public List<int> SucceedFastExitCodes { get; } = new();
        public List<int> UncacheableExitCodes { get; } = new();
        public TimeSpan? Timeout { get; set; }
        public TimeSpan? WarningTimeout { get; set; }
        public int Weight { get; set; }
        public int Priority { get; set; }
        public int ProcessRetries { get; set; }
        public StringId WarningRegex { get; set; }
        public StringId ErrorRegex { get; set; }
        public bool EnableMultiLineErrorScanning { get; set; }
        public AbsolutePath StandardOutput { get; set; }
        public AbsolutePath StandardError { get; set; }
        public AbsolutePath TraceFile { get; set; }
        public StringId RetryAttemptEnvironmentVariable { get; set; }
    }

    public readonly record struct DirectGraphFileOutput(
        AbsolutePath Path, FileExistence Existence = FileExistence.Required, FileArtifact Rewrites = default);
    public readonly record struct DirectGraphDirectoryOutput(AbsolutePath Path, NativeSealKind Kind);
    public readonly record struct DirectGraphOrderDependency(FileArtifact File, PipId Producer);
    public readonly record struct DirectGraphEnvironmentVariable(StringId Name, StringId Value = default, bool IsPassThrough = false);
    public readonly record struct DirectGraphSemaphore(StringId Name, int Limit, int IncrementBy);
    public readonly record struct DirectGraphBreakawayChildProcess(StringId ProcessName, string RequiredArguments, bool RequiredArgumentsIgnoreCase);

    public sealed class DirectGraphWriteFile : DirectGraphOperation
    {
        public AbsolutePath Destination { get; set; }
        public PipData Content { get; set; }
    }

    public sealed class DirectGraphCopyFile : DirectGraphOperation
    {
        public FileArtifact Source { get; set; }
        public AbsolutePath Destination { get; set; }
        public bool OutputsMustRemainWritable { get; set; }
    }

    public sealed class DirectGraphSealDirectory : DirectGraphOperation
    {
        public AbsolutePath DirectoryRoot { get; set; }
        public NativeSealKind Kind { get; set; }
        public List<FileArtifact> Contents { get; } = new();
        public List<DirectoryArtifact> OutputDirectoryContents { get; } = new();
        public List<StringId> Patterns { get; } = new();
        public List<StringId> SourceExclusions { get; } = new();
        public bool Scrub { get; set; }
    }

    public sealed class DirectGraphCompositeDirectory : DirectGraphOperation
    {
        public AbsolutePath DirectoryRoot { get; set; }
        public List<DirectoryArtifact> Contents { get; } = new();
        public SealDirectoryCompositionActionKind Action { get; set; }
        public SealDirectoryContentFilter? ContentFilter { get; set; }
    }

    public sealed class DirectGraphExport : DirectGraphOperation
    {
        public List<FileArtifact> Files { get; } = new();
        public List<DirectoryArtifact> Directories { get; } = new();
    }

    /// <summary>Artifacts and identity returned by construction, not a transport response.</summary>
    public sealed record DirectGraphConstructionResult(FileArtifact[] Files, DirectoryArtifact[] Directories, PipId PipId = default, long SemiStableHash = 0);

    public sealed class DirectGraphInvalidRequestException : Exception
    {
        public DirectGraphInvalidRequestException(string message) : base(message) { }
    }

    public sealed class DirectGraphConstructionException : Exception
    {
        public DirectGraphConstructionException(string message) : base(message) { }
    }
}

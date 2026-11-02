// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;
using BuildXL.Pips.Operations;
using BuildXL.Scheduler.Tracing;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using Test.BuildXL.Executables.TestProcess;
using Test.BuildXL.Scheduler;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using ProcessesLogEventId = BuildXL.Processes.Tracing.LogEventId;

namespace IntegrationTest.BuildXL.Scheduler
{
    [Feature(Features.SealedSourceDirectory)]
    [TestClassIfSupported(requiresSandbox: true)]
    public class SealedSourceDirectoryTests : SchedulerIntegrationTestBase
    {
        public SealedSourceDirectoryTests(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [InlineData(".git/config", false)]
        [InlineData("nested/.git/config", false)]
        [InlineData("private/state", false)]
        [InlineData(".git/config", true)]
        public void ExcludedReadsRequireAnotherInputGrant(string relativePath, bool explicitlyDeclared)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.TreatStatAsProbe = false;
            var source = CreateSourceFile(Path.Combine(SourceRoot, relativePath));
            var builder = CreatePipBuilder(new[]
            {
                Operation.ReadFile(source, doNotInfer: !explicitlyDeclared),
                Operation.WriteFile(CreateOutputFileArtifact()),
            });
            builder.AddInputDirectory(SealWithExclusions());
            var pip = SchedulePipBuilder(builder).Process;

            if (explicitlyDeclared)
            {
                RunScheduler().AssertCacheMiss(pip.PipId);
                RunScheduler().AssertCacheHit(pip.PipId);
            }
            else
            {
                AssertExcludedAccessFails();
            }
        }

        [TheoryIfSupported(requiresEBPFEnabled: true)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ExactExcludedStatHonorsProbeSetting(bool isDirectory, bool treatStatAsProbe)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.TreatStatAsProbe = treatStatAsProbe;
            var target = SourceRootPath.Combine(Context.PathTable, ".git");
            if (isDirectory)
            {
                Directory.CreateDirectory(target.ToString(Context.PathTable));
            }
            else
            {
                CreateSourceFile(target.ToString(Context.PathTable));
            }

            var builder = CreatePipBuilder(new[]
            {
                Operation.SpawnExe(Context.PathTable,
                    FileArtifact.CreateSourceFile(AbsolutePath.Create(Context.PathTable, "/usr/bin/stat")),
                    arguments: target.ToString(Context.PathTable)),
                Operation.WriteFile(CreateOutputFileArtifact()),
            });
            builder.AddInputDirectory(SealWithExclusions());
            var pip = SchedulePipBuilder(builder).Process;

            if (treatStatAsProbe)
            {
                RunScheduler().AssertCacheMiss(pip.PipId);
                RunScheduler().AssertCacheHit(pip.PipId);
            }
            else
            {
                AssertExcludedAccessFails();
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ProbesBelowExcludedEntryFailEvenWhenAbsent(bool exists)
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.TreatStatAsProbe = true;
            var target = SourceRootPath.Combine(Context.PathTable, RelativePath.Create(Context.StringTable, ".git/config"));
            if (exists)
            {
                CreateSourceFile(target.ToString(Context.PathTable));
            }

            var builder = CreatePipBuilder(new[]
            {
                Operation.Probe(FileArtifact.CreateSourceFile(target), doNotInfer: true),
                Operation.WriteFile(CreateOutputFileArtifact()),
            });
            builder.AddInputDirectory(SealWithExclusions());
            SchedulePipBuilder(builder);
            AssertExcludedAccessFails();
        }

        [Fact]
        public void ExcludedDirectoryCannotBeEnumerated()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.TreatStatAsProbe = true;
            var directory = DirectoryArtifact.CreateWithZeroPartialSealId(SourceRootPath.Combine(Context.PathTable, ".git"));
            Directory.CreateDirectory(directory.Path.ToString(Context.PathTable));
            var builder = CreatePipBuilder(new[]
            {
                Operation.EnumerateDir(directory, doNotInfer: true),
                Operation.WriteFile(CreateOutputFileArtifact()),
            });
            builder.AddInputDirectory(SealWithExclusions());
            SchedulePipBuilder(builder);
            AssertExcludedAccessFails();
        }

        [Fact]
        public void ParentEnumerationTracksExcludedNameButNotItsContents()
        {
            Configuration.Sandbox.UnsafeSandboxConfigurationMutable.TreatStatAsProbe = true;
            var root = DirectoryArtifact.CreateWithZeroPartialSealId(SourceRootPath);
            var builder = CreatePipBuilder(new[]
            {
                Operation.EnumerateDir(root, doNotInfer: true),
                Operation.WriteFile(CreateOutputFileArtifact()),
            });
            builder.AddInputDirectory(SealWithExclusions());
            var pip = SchedulePipBuilder(builder).Process;
            RunScheduler().AssertCacheMiss(pip.PipId);
            RunScheduler().AssertCacheHit(pip.PipId);

            var metadata = CreateSourceFile(Path.Combine(SourceRoot, ".git", "config"));
            RunScheduler().AssertCacheMiss(pip.PipId);
            WriteSourceFile(metadata);
            RunScheduler().AssertCacheHit(pip.PipId);
            Directory.Delete(Path.Combine(SourceRoot, ".git"), recursive: true);
            RunScheduler().AssertCacheHit(pip.PipId);
        }

        private DirectoryArtifact SealWithExclusions()
        {
            var seal = new SealDirectory(
                SourceRootPath,
                CollectionUtilities.EmptySortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer>(OrdinalFileArtifactComparer.Instance),
                CollectionUtilities.EmptySortedReadOnlyArray<DirectoryArtifact, OrdinalDirectoryArtifactComparer>(OrdinalDirectoryArtifactComparer.Instance),
                SealDirectoryKind.SourceAllDirectories, CreateProvenance(),
                ReadOnlyArray<StringId>.Empty, ReadOnlyArray<StringId>.Empty,
                sourceExclusions: new SourceSealExclusions(
                    ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(Context.StringTable, "private/state") }),
                    ReadOnlyArray<PathAtom>.From(new[] { PathAtom.Create(Context.StringTable, ".git") })));
            return PipGraphBuilder.AddSealDirectory(seal);
        }

        private void AssertExcludedAccessFails()
        {
            RunScheduler().AssertFailure();
            AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess, allowMore: true);
            AssertVerboseEventLogged(LogEventId.DependencyViolationMissingSourceDependency, allowMore: true);
            AssertWarningEventLogged(LogEventId.ProcessNotStoredToCacheDueToFileMonitoringViolations);
            AssertErrorEventLogged(LogEventId.FileMonitoringError);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ValidateCachingBehaviorTopOnlySealedSourceDirectory(bool probeOnly)
        {
            ValidateCachingBehavior(topOnly: true, probeOnly: probeOnly);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ValidateCachingBehaviorNestedSealedSourceDirectory(bool probeOnly)
        {
            ValidateCachingBehavior(topOnly: false, probeOnly:probeOnly);
        }

        [Fact]
        public void ConsumeNestedFileUnderTopOnlyMount()
        {
            var source = CreateSourceFile(Path.Combine(SourceRoot, "nested", "someFile.txt"));
            WriteSourceFile(source);
            var output = CreateOutputFileArtifact(ObjectRoot);

            DirectoryArtifact dir = SealDirectory(SourceRootPath, SealDirectoryKind.SourceTopDirectoryOnly);

            var builder = CreatePipBuilder(new Operation[]
                {
                    Operation.ReadFile(source, doNotInfer: true),
                    Operation.WriteFile(output),
                });
            builder.AddInputDirectory(dir);
            SchedulePipBuilder(builder);

            RunScheduler().AssertFailure();
            AssertVerboseEventLogged(LogEventId.DisallowedFileAccessInTopOnlySourceSealedDirectory);
            AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess);
            AssertVerboseEventLogged(LogEventId.DependencyViolationMissingSourceDependency);
            AssertWarningEventLogged(LogEventId.ProcessNotStoredToCacheDueToFileMonitoringViolations);
            AssertErrorEventLogged(LogEventId.FileMonitoringError);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ConsumeFilterPassingFile(bool topOnly)
        {
            FileArtifact source;
            if (topOnly)
            {
                source = FileArtifact.CreateSourceFile(SourceRootPath.Combine(Context.PathTable, "file.txt"));
            }
            else
            {
                var nestedDir = AbsolutePath.Create(Context.PathTable, Path.Combine(SourceRoot, "nested"));
                source = FileArtifact.CreateSourceFile(nestedDir.Combine(Context.PathTable, "file.txt"));
            }

            WriteSourceFile(source);
            var output = CreateOutputFileArtifact(ObjectRoot);

            SealDirectory sealedDirectory = CreateSourceSealDirectory(SourceRootPath, topOnly ? SealDirectoryKind.SourceTopDirectoryOnly : SealDirectoryKind.SourceAllDirectories, "*.txt", "*.cs");
            DirectoryArtifact dir = PipGraphBuilder.AddSealDirectory(sealedDirectory);

            var builder = CreatePipBuilder(new Operation[]
                {
                    Operation.ReadFile(source, doNotInfer: true),
                    Operation.WriteFile(output),
                });
            builder.AddInputDirectory(dir);
            SchedulePipBuilder(builder);

            RunScheduler().AssertSuccess();
        }

        [Fact]
        public void ConsumeFilterNotPassingFile()
        {
            FileArtifact source = FileArtifact.CreateSourceFile(SourceRootPath.Combine(Context.PathTable, "file.txt"));
            WriteSourceFile(source);
            var output = CreateOutputFileArtifact(ObjectRoot);

            SealDirectory sealedDirectory = CreateSourceSealDirectory(SourceRootPath, SealDirectoryKind.SourceTopDirectoryOnly, "*.cs");
            DirectoryArtifact dir = PipGraphBuilder.AddSealDirectory(sealedDirectory);

            var builder = CreatePipBuilder(new Operation[]
                {
                    Operation.ReadFile(source, doNotInfer: true),
                    Operation.WriteFile(output),
                });
            builder.AddInputDirectory(dir);
            SchedulePipBuilder(builder);

            RunScheduler().AssertFailure();
            AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess);
            AssertVerboseEventLogged(LogEventId.DependencyViolationMissingSourceDependency);
            AssertWarningEventLogged(LogEventId.ProcessNotStoredToCacheDueToFileMonitoringViolations);
            AssertErrorEventLogged(LogEventId.FileMonitoringError);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void InputOverlappingSealedSourceDirectory(bool probeOnly)
        {
            ValidateCachingBehavior(topOnly: false, probeOnly: probeOnly, alsoDeclareAsInput: true);
        }

        [Feature(Features.AbsentFileProbe)]
        [Fact]
        public void ProbeWithinSealedSourceDirectory()
        {
            var output = CreateOutputFileArtifact(ObjectRoot);
            var input = CreateSourceFile(SourceRoot);
            var absentPath = FileArtifact.CreateSourceFile(CreateUniqueSourcePath(SourceRoot));
            DirectoryArtifact dir = SealDirectory(SourceRootPath, SealDirectoryKind.SourceAllDirectories);

            var builder = CreatePipBuilder(new Operation[]
                {
                    Operation.ReadFile(input),
                    Operation.ReadFile(absentPath, doNotInfer: true),
                    Operation.WriteFile(output)
                });
            builder.AddInputDirectory(dir);
            Process p = SchedulePipBuilder(builder).Process;

            RunScheduler().AssertSuccess();
            RunScheduler().AssertCacheHit(p.PipId);

            // Make the absent path exist and make sure it is cached appropriately
            WriteSourceFile(absentPath);
            RunScheduler().AssertFailure();
            AssertVerboseEventLogged(ProcessesLogEventId.PipProcessDisallowedFileAccess, allowMore: true);
            AssertVerboseEventLogged(LogEventId.DependencyViolationMissingSourceDependency);
            AssertWarningEventLogged(LogEventId.ProcessNotStoredToCacheDueToFileMonitoringViolations);
            AssertErrorEventLogged(LogEventId.FileMonitoringError);
        }

        [Fact]
        public void TestSourceDirectoryUsedAsInputFails()
        {
            var sourceDirectory = DirectoryArtifact.CreateWithZeroPartialSealId(CreateUniqueSourcePath("src-dir"));
            var builder = CreatePipBuilder(new Operation[]
                {
                    Operation.WriteFile(CreateOutputFileArtifact())
                });
            builder.ArgumentsBuilder.Add(sourceDirectory);
            builder.AddInputDirectory(sourceDirectory);
            Assert.Throws<BuildXLTestException>(() => SchedulePipBuilder(builder));
            AssertErrorEventLogged(global::BuildXL.Pips.Tracing.LogEventId.SourceDirectoryUsedAsDependency);
        }

        /// <param name="topOnly">When true, the SourceSealedDirectory will be a topOnly. When false, it will be a recursive SourceSealedDirectory</param>
        /// <param name="probeOnly">When true, the source file will be probed without reading</param>
        /// <param name="alsoDeclareAsInput">When true, the file accessed under the SourceSealDirectory will also be declared as an input</param>
        public void ValidateCachingBehavior(bool topOnly, bool probeOnly, bool alsoDeclareAsInput = false)
        {
            // Create a very simple graph with a process that consumes files from a source sealed directory
            var source1 = topOnly ? CreateSourceFile(SourceRoot) : CreateSourceFile(Path.Combine(SourceRoot, "nested"));
            WriteSourceFile(source1);

            // Create a graph with a SealedSourceDirectory
            DirectoryArtifact dir = SealDirectory(SourceRootPath, topOnly ? SealDirectoryKind.SourceTopDirectoryOnly : SealDirectoryKind.SourceAllDirectories);

            // Set up test process
            var builder = CreatePipBuilder(new Operation[]
                {
                    Operation.WriteFile(CreateOutputFileArtifact(ObjectRoot)),
                    // don't take dependencies on files in SealedSourceDirectory
                    probeOnly ? Operation.Probe(source1, doNotInfer: !alsoDeclareAsInput) :  Operation.ReadFile(source1, doNotInfer: !alsoDeclareAsInput)
                });
            builder.AddInputDirectory(dir);
            Process process = SchedulePipBuilder(builder).Process;

            // Perform builds:
            RunScheduler().AssertCacheMiss(process.PipId);
            RunScheduler().AssertCacheHit(process.PipId);

            // Modify an input file and make sure there's a cache miss
            WriteSourceFile(source1);

            if (probeOnly && !alsoDeclareAsInput)
            {
                // Files probes that are not declared as input do not cause cache misses when the content changes.
                RunScheduler().AssertCacheHit(process.PipId);
            }
            else
            {
                RunScheduler().AssertCacheMiss(process.PipId);
            }

            RunScheduler().AssertCacheHit(process.PipId);
        }
    }
}

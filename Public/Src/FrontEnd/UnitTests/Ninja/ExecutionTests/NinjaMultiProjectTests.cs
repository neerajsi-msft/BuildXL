// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;
using System.Linq;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using Test.BuildXL.EngineTestUtilities;
using Test.BuildXL.FrontEnd.Ninja.Infrastructure;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;

namespace Test.BuildXL.FrontEnd.Ninja
{
    [TestClassIfSupported(requiresLinuxBasedOperatingSystem: true)]
    public sealed class NinjaMultiProjectTests : NinjaPipExecutionTestBase
    {
        public NinjaMultiProjectTests(ITestOutputHelper output) : base(output) { }

        private ICommandLineConfiguration CreateConfiguration(string producerValue = "producer", string consumerValue = "consumer")
        {
            return Build()
                .Configuration($@"
config({{
    resolvers: [{{
        kind: 'Ninja',
        moduleName: 'Grouped',
        projects: [
            {{
                name: 'consumer',
                root: d`consumer`,
                environment: Map.empty<string, string>()
                    .add('PATH', '/usr/bin:/bin').add('VALUE', '{consumerValue}'),
            }},
            {{
                name: 'producer',
                root: d`producer`,
                environment: Map.empty<string, string>()
                    .add('PATH', '/usr/bin:/bin').add('VALUE', '{producerValue}')
                    .add('PRODUCER_ONLY', 'must-not-leak'),
            }},
        ],
        additionalOutputDirectories: [p`sdk`],
    }}],
    mounts: [{{
        name: a`Workspace`, path: p`.`,
        trackSourceFileChanges: true, isWritable: true, isReadable: true,
    }}],
}});")
                .AddSpec(Path.Combine(SourceRoot, "producer", "build.ninja"), @"
rule produce
  command = mkdir -p ../sdk && printf '%s:' ""$$VALUE"" > $out && cat local.txt >> $out
build ../sdk/shared.txt: produce local.txt
default ../sdk/shared.txt
")
                .AddSpec(Path.Combine(SourceRoot, "consumer", "build.ninja"), @"
rule consume
  command = printf '%s:%s:' ""$$VALUE"" ""$${PRODUCER_ONLY-unset}"" > $out && cat local.txt ../sdk/shared.txt >> $out
build final.txt: consume local.txt ../sdk/shared.txt
default final.txt
")
                .AddSpec(Path.Combine(SourceRoot, "producer", "local.txt"), "producer-local")
                .AddSpec(Path.Combine(SourceRoot, "consumer", "local.txt"), "consumer-local:")
                .PersistSpecsAndGetConfiguration();
        }

        [Fact]
        public void StitchesGraphsWithIndependentRootsAndEnvironments()
        {
            var result = RunEngineWithConfig(CreateConfiguration());

            Assert.True(result.IsSuccess);
            Assert.Equal("consumer:unset:consumer-local:producer:producer-local",
                File.ReadAllText(Path.Combine(SourceRoot, "consumer", "final.txt")));
            var processes = result.EngineState.PipGraph.RetrievePipsOfType(PipType.Process).ToList();
            Assert.Equal(2, processes.Count);
            var consumer = processes.OfType<Process>().Single(process => process.Dependencies.Any(
                input => input.Path.GetName(PathTable).ToString(PathTable.StringTable) == "shared.txt"));
            Assert.True(consumer.Dependencies.Single(
                input => input.Path.GetName(PathTable).ToString(PathTable.StringTable) == "shared.txt").IsOutputFile);
        }

        [Fact]
        public void BuildsWithReadonlyProjectRootAndSeparateOutputs()
        {
            var config = Build()
                .Configuration(@"
config({
    resolvers: [{
        kind: 'Ninja',
        moduleName: 'Readonly',
        allowWritableSourceDirectory: false,
        projects: [{
            name: 'source',
            root: d`source`,
            environment: Map.empty<string, string>().add('PATH', '/usr/bin:/bin'),
        }],
        additionalOutputDirectories: [p`out`],
    }],
    mounts: [{
        name: a`Source`, path: p`.`,
        trackSourceFileChanges: true, isWritable: false, isReadable: true,
    }, {
        name: a`Outputs`, path: p`out`,
        trackSourceFileChanges: true, isWritable: true, isReadable: true,
    }],
});")
                .AddSpec(Path.Combine(SourceRoot, "source", "build.ninja"), @"
rule copy
  command = mkdir -p ../out && cat input.txt > $out
build ../out/result.txt: copy input.txt
default ../out/result.txt
")
                .AddSpec(Path.Combine(SourceRoot, "source", "input.txt"), "original")
                .PersistSpecsAndGetConfiguration();

            var result = RunEngineWithConfig(config);

            Assert.True(result.IsSuccess);
            Assert.Equal("original", File.ReadAllText(Path.Combine(SourceRoot, "out", "result.txt")));
            Assert.Equal("original", File.ReadAllText(Path.Combine(SourceRoot, "source", "input.txt")));
        }

        [Fact]
        public void ProjectEnvironmentChangesInvalidateCachedResults()
        {
            var cache = new TestCache();
            var config = CreateConfiguration();
            ((CommandLineConfiguration)config).Cache.CacheGraph = false;
            ((CommandLineConfiguration)config).Cache.AllowFetchingCachedGraphFromContentCache = false;
            var configPath = config.Startup.ConfigFile.ToString(PathTable);
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            File.WriteAllText(configPath, File.ReadAllText(configPath)
                .Replace(".add('VALUE', 'producer')", ".add('VALUE', 'changed-producer')")
                .Replace(".add('VALUE', 'consumer')", ".add('VALUE', 'changed-consumer')"));
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);

            Assert.Equal("changed-consumer:unset:consumer-local:changed-producer:producer-local",
                File.ReadAllText(Path.Combine(SourceRoot, "consumer", "final.txt")));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void IndependentProjectInsertionPreservesExistingPipIdentities(bool prepend)
        {
            var config = CreateConfiguration();
            ((CommandLineConfiguration)config).Cache.CacheGraph = false;
            ((CommandLineConfiguration)config).Cache.AllowFetchingCachedGraphFromContentCache = false;
            var cache = new TestCache();
            var first = RunEngineWithConfig(config, cache);
            Assert.True(first.IsSuccess);
            var before = first.EngineState.PipGraph.RetrievePipsOfType(PipType.Process).OfType<Process>()
                .ToDictionary(process => process.FileOutputs[0].Path,
                    process => (process.SemiStableHash, process.TempDirectory));
            Assert.Equal(2, before.Count);

            var configPath = config.Startup.ConfigFile.ToString(PathTable);
            var configuration = File.ReadAllText(configPath);
            const string project = @"
            {
                name: 'independent',
                root: d`independent`,
                specFile: f`independent/unrelated.ninja`,
                environment: Map.empty<string, string>().add('PATH', '/usr/bin:/bin'),
            },";
            configuration = prepend
                ? configuration.Replace("projects: [", "projects: [" + project)
                : configuration.Replace("        ],", project + "\n        ],");
            Directory.CreateDirectory(Path.Combine(SourceRoot, "independent"));
            File.WriteAllText(Path.Combine(SourceRoot, "independent", "unrelated.ninja"),
                "rule independent\n  command = printf unrelated > $out\nbuild result.txt: independent\ndefault result.txt\n");
            File.WriteAllText(configPath, configuration);

            var second = RunEngineWithConfig(config, cache);
            Assert.True(second.IsSuccess);
            Assert.Equal(2, second.EnginePerformanceInfo.SchedulerPerformanceInfo.ProcessPipCacheHits);
            Assert.Equal(1, second.EnginePerformanceInfo.SchedulerPerformanceInfo.ProcessPipCacheMisses);
            var processes = second.EngineState.PipGraph.RetrievePipsOfType(PipType.Process).OfType<Process>().ToList();
            Assert.Equal(3, processes.Count);
            foreach (var entry in before)
            {
                var process = processes.Single(candidate => candidate.FileOutputs[0].Path == entry.Key);
                Assert.True(process.TempDirectory.IsValid);
                Assert.Equal(entry.Value.SemiStableHash, process.SemiStableHash);
                Assert.Equal(entry.Value.TempDirectory, process.TempDirectory);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RejectsAmbiguousOrCyclicGroups(bool cycle)
        {
            var config = CreateConfiguration();
            var producerPath = Path.Combine(SourceRoot, "producer", "build.ninja");
            var consumerPath = Path.Combine(SourceRoot, "consumer", "build.ninja");
            File.WriteAllText(producerPath, "rule r\n  command = printf x > $out\n" +
                $"build ../sdk/shared.txt: r{(cycle ? " ../consumer/final.txt" : "")}\ndefault ../sdk/shared.txt\n");
            File.WriteAllText(consumerPath, "rule r\n  command = printf x > $out\n" +
                (cycle
                    ? "build final.txt: r ../sdk/shared.txt\ndefault final.txt\n"
                    : "build ../sdk/shared.txt: r\ndefault ../sdk/shared.txt\n"));

            Assert.False(RunEngineWithConfig(config).IsSuccess);
            AssertErrorEventLogged(global::BuildXL.FrontEnd.Ninja.Tracing.LogEventId.InvalidResolverSettings);
            AssertLogContains(false, cycle ? "Cycle in stitched Ninja graphs" : "Duplicate Ninja producer");
        }

        private ICommandLineConfiguration CreateDirectoryConfiguration(string export = "sdk", string producer = "producer")
        {
            var config = CreateConfiguration();
            var path = config.Startup.ConfigFile.ToString(PathTable);
            File.WriteAllText(path, File.ReadAllText(path)
                .Replace("name: 'producer',", $"name: 'producer', directoryExports: [p`{export}`],")
                .Replace("name: 'consumer',", $"name: 'consumer', directoryImports: Map.empty<string, Path[]>().add('{producer}', [p`sdk`]),"));
            File.WriteAllText(Path.Combine(SourceRoot, "consumer", "build.ninja"),
                "rule consume\n  command = cat ../sdk/shared.txt > $out\nbuild final.txt: consume\ndefault final.txt\n");
            return config;
        }

        [Theory]
        [InlineData("producer")]
        [InlineData("PrOdUcEr")]
        public void DirectoryImportsUseProducedArtifactsWithoutExplicitMemberInputs(string producer)
        {
            var result = RunEngineWithConfig(CreateDirectoryConfiguration(producer: producer));
            Assert.True(result.IsSuccess);
            Assert.Equal("producer:producer-local", File.ReadAllText(Path.Combine(SourceRoot, "consumer", "final.txt")));
            var processes = result.EngineState.PipGraph.RetrievePipsOfType(PipType.Process).OfType<Process>().ToList();
            var consumer = processes.Single(process => process.DirectoryDependencies.Length != 0);
            Assert.DoesNotContain(consumer.Dependencies, file => file.Path.GetName(PathTable).ToString(PathTable.StringTable) == "shared.txt");
            Assert.Single(consumer.DirectoryDependencies);
            Assert.Contains(result.EngineState.PipGraph.RetrievePipsOfType(PipType.SealDirectory).OfType<SealDirectory>(),
                seal => seal.Kind == SealDirectoryKind.Partial && seal.Contents.Any(file => file.IsOutputFile));
        }

        [Theory]
        [InlineData("empty", "producer", "empty or contains outputs")]
        [InlineData("sdk", "unknown", "missing, unknown, or self-owned")]
        public void RejectsInvalidDirectoryContracts(string export, string producer, string diagnostic)
        {
            Assert.False(RunEngineWithConfig(CreateDirectoryConfiguration(export, producer)).IsSuccess);
            AssertErrorEventLogged(global::BuildXL.FrontEnd.Ninja.Tracing.LogEventId.InvalidResolverSettings);
            AssertLogContains(false, diagnostic);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DynamicDirectoryOutputsDiscoverMembersAndReplayCache(bool explicitMemberInput)
        {
            var config = CreateDirectoryConfiguration();
            ((CommandLineConfiguration)config).Cache.CacheGraph = false;
            ((CommandLineConfiguration)config).Cache.AllowFetchingCachedGraphFromContentCache = false;
            var configPath = config.Startup.ConfigFile.ToString(PathTable);
            File.WriteAllText(configPath, File.ReadAllText(configPath)
                .Replace("directoryExports:", "directoryOutputs:"));
            File.WriteAllText(Path.Combine(SourceRoot, "producer", "build.ninja"),
                "rule produce\n  command = mkdir -p ../sdk && cat local.txt > ../sdk/dynamic.txt && touch $out\n" +
                "build done: produce local.txt\ndefault done\n");
            File.WriteAllText(Path.Combine(SourceRoot, "consumer", "build.ninja"),
                "rule consume\n  command = cat ../sdk/dynamic.txt > $out\nbuild final.txt: consume" +
                (explicitMemberInput ? " ../sdk/dynamic.txt" : "") + "\ndefault final.txt\n");

            var cache = new TestCache();
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            var final = Path.Combine(SourceRoot, "consumer", "final.txt");
            Assert.Equal("producer-local", File.ReadAllText(final));
            File.Delete(final);
            File.Delete(Path.Combine(SourceRoot, "sdk", "dynamic.txt"));
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            Assert.Equal("producer-local", File.ReadAllText(final));

            File.WriteAllText(Path.Combine(SourceRoot, "producer", "local.txt"), "changed");
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            Assert.Equal("changed", File.ReadAllText(final));
        }

        [Fact]
        public void RejectsAmbiguousDynamicDirectoryProducer()
        {
            var config = CreateDirectoryConfiguration();
            var configPath = config.Startup.ConfigFile.ToString(PathTable);
            File.WriteAllText(configPath, File.ReadAllText(configPath)
                .Replace("directoryExports:", "directoryOutputs:"));
            File.AppendAllText(Path.Combine(SourceRoot, "producer", "build.ninja"),
                "\nbuild extra: produce local.txt\ndefault extra\n");
            Assert.False(RunEngineWithConfig(config).IsSuccess);
            AssertErrorEventLogged(global::BuildXL.FrontEnd.Ninja.Tracing.LogEventId.InvalidResolverSettings);
            AssertLogContains(false, "exactly one executable node");
        }

        [Fact]
        public void EmptyDynamicDirectoriesCanBeImported()
        {
            var config = CreateDirectoryConfiguration();
            var configPath = config.Startup.ConfigFile.ToString(PathTable);
            File.WriteAllText(configPath, File.ReadAllText(configPath)
                .Replace("directoryExports:", "directoryOutputs:"));
            File.WriteAllText(Path.Combine(SourceRoot, "producer", "build.ninja"),
                "rule produce\n  command = mkdir -p ../sdk && touch $out\nbuild done: produce\ndefault done\n");
            File.WriteAllText(Path.Combine(SourceRoot, "consumer", "build.ninja"),
                "rule consume\n  command = test -d ../sdk && printf empty > $out\nbuild final.txt: consume\ndefault final.txt\n");
            var cache = new TestCache();
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            Directory.Delete(Path.Combine(SourceRoot, "sdk"));
            File.Delete(Path.Combine(SourceRoot, "consumer", "final.txt"));
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            Assert.Equal("empty", File.ReadAllText(Path.Combine(SourceRoot, "consumer", "final.txt")));
        }

        [Fact]
        public void DynamicImportsPreserveOtherProducersExplicitFileInputs()
        {
            var config = CreateConfiguration();
            var configPath = config.Startup.ConfigFile.ToString(PathTable);
            File.WriteAllText(configPath, File.ReadAllText(configPath)
                .Replace("name: 'consumer',",
                    "name: 'consumer', directoryImports: Map.empty<string, Path[]>().add('other', [p`sdk`]),")
                .Replace("projects: [", @"projects: [{
                    name: 'other', root: d`other`, directoryOutputs: [p`sdk`],
                    environment: Map.empty<string, string>().add('PATH', '/usr/bin:/bin'),
                },"));
            Directory.CreateDirectory(Path.Combine(SourceRoot, "other"));
            File.WriteAllText(Path.Combine(SourceRoot, "other", "build.ninja"),
                "rule produce\n  command = mkdir -p ../sdk && printf dynamic: > ../sdk/dynamic.txt && touch $out\n" +
                "build done: produce\ndefault done\n");
            File.WriteAllText(Path.Combine(SourceRoot, "consumer", "build.ninja"),
                "rule consume\n  command = cat ../sdk/dynamic.txt ../sdk/shared.txt > $out\n" +
                "build final.txt: consume ../sdk/dynamic.txt ../sdk/shared.txt\ndefault final.txt\n");
            var result = RunEngineWithConfig(config);
            Assert.True(result.IsSuccess);
            Assert.Equal("dynamic:producer:producer-local",
                File.ReadAllText(Path.Combine(SourceRoot, "consumer", "final.txt")));
            var consumer = result.EngineState.PipGraph.RetrievePipsOfType(PipType.Process).OfType<Process>()
                .Single(process => process.DirectoryDependencies.Length != 0);
            Assert.Contains(consumer.Dependencies, file => file.IsOutputFile &&
                file.Path.GetName(PathTable).ToString(PathTable.StringTable) == "shared.txt");
            Assert.DoesNotContain(consumer.Dependencies, file =>
                file.Path.GetName(PathTable).ToString(PathTable.StringTable) == "dynamic.txt");
        }

        [Fact]
        public void DynamicMembershipChangesRemoveStaleMembers()
        {
            var config = CreateDirectoryConfiguration();
            var configPath = config.Startup.ConfigFile.ToString(PathTable);
            File.WriteAllText(configPath, File.ReadAllText(configPath)
                .Replace("directoryExports:", "directoryOutputs:"));
            File.WriteAllText(Path.Combine(SourceRoot, "producer", "build.ninja"),
                "rule produce\n  command = mkdir -p ../sdk && cat local.txt > ../sdk/$$(cat local.txt).h && touch $out\n" +
                "build done: produce local.txt\ndefault done\n");
            File.WriteAllText(Path.Combine(SourceRoot, "consumer", "build.ninja"),
                "rule consume\n  command = cat ../sdk/*.h > $out\nbuild final.txt: consume\ndefault final.txt\n");
            var cache = new TestCache();
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            File.WriteAllText(Path.Combine(SourceRoot, "producer", "local.txt"), "renamed");
            Assert.True(RunEngineWithConfig(config, cache).IsSuccess);
            Assert.Equal("renamed", File.ReadAllText(Path.Combine(SourceRoot, "consumer", "final.txt")));
            Assert.False(File.Exists(Path.Combine(SourceRoot, "sdk", "producer-local.h")));
            Assert.True(File.Exists(Path.Combine(SourceRoot, "sdk", "renamed.h")));
        }

        [Fact]
        public void StaticDirectoryImportsPreserveExplicitSourceFileInputs()
        {
            var config = CreateDirectoryConfiguration();
            Directory.CreateDirectory(Path.Combine(SourceRoot, "sdk"));
            File.WriteAllText(Path.Combine(SourceRoot, "sdk", "source.txt"), "source:");
            File.WriteAllText(Path.Combine(SourceRoot, "consumer", "build.ninja"),
                "rule consume\n  command = cat ../sdk/source.txt ../sdk/shared.txt > $out\n" +
                "build final.txt: consume ../sdk/source.txt\ndefault final.txt\n");
            var result = RunEngineWithConfig(config);
            Assert.True(result.IsSuccess);
            Assert.Equal("source:producer:producer-local",
                File.ReadAllText(Path.Combine(SourceRoot, "consumer", "final.txt")));
            var consumer = result.EngineState.PipGraph.RetrievePipsOfType(PipType.Process).OfType<Process>()
                .Single(process => process.DirectoryDependencies.Length != 0);
            Assert.Contains(consumer.Dependencies, file => file.IsSourceFile &&
                file.Path.GetName(PathTable).ToString(PathTable.StringTable) == "source.txt");
        }

        [Theory]
        [InlineData("directoryExports: [p`sdk`],", "empty or contains outputs")]
        [InlineData("directoryExports: [p`consumer`, p`consumer/subdir`],", "Overlapping Ninja directory exports")]
        public void RejectsInvalidOrOverlappingDirectoryExports(string declaration, string diagnostic)
        {
            var config = CreateDirectoryConfiguration();
            var path = config.Startup.ConfigFile.ToString(PathTable);
            File.WriteAllText(path, File.ReadAllText(path).Replace("name: 'consumer',", $"name: 'consumer', {declaration}"));
            Assert.False(RunEngineWithConfig(config).IsSuccess);
            AssertErrorEventLogged(global::BuildXL.FrontEnd.Ninja.Tracing.LogEventId.InvalidResolverSettings);
            AssertLogContains(false, diagnostic);
        }

        [Fact]
        public void RejectsDirectoryImportCycles()
        {
            var config = CreateDirectoryConfiguration();
            var path = config.Startup.ConfigFile.ToString(PathTable);
            File.WriteAllText(path, File.ReadAllText(path)
                .Replace("name: 'consumer',", "name: 'consumer', directoryExports: [p`consumer`],")
                .Replace("name: 'producer',", "name: 'producer', directoryImports: Map.empty<string, Path[]>().add('consumer', [p`consumer`]),"));
            Assert.False(RunEngineWithConfig(config).IsSuccess);
            AssertErrorEventLogged(global::BuildXL.FrontEnd.Ninja.Tracing.LogEventId.InvalidResolverSettings);
            AssertLogContains(false, "Cycle in stitched Ninja graphs");
        }
    }
}

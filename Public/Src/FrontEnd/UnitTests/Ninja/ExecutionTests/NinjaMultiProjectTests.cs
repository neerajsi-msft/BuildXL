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

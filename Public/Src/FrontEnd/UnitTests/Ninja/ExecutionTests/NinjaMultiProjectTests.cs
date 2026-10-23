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
    }
}

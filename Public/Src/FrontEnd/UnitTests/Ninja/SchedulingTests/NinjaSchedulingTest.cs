using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using Test.BuildXL.FrontEnd.Ninja.Infrastructure;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using BuildXL.FrontEnd.Ninja;
using BuildXL.FrontEnd.Ninja.Serialization;
using BuildXL.Utilities.Configuration.Resolvers;
using BuildXL.Pips.Operations;
using Test.BuildXL.FrontEnd.Core;
using static Test.BuildXL.TestUtilities.TestEnv;

namespace Test.BuildXL.FrontEnd.Ninja.SchedulingTests
{
    public class NinjaSchedulingTest : NinjaPipSchedulingTestBase
    {
        public NinjaSchedulingTest(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact]
        public void SourceSealsCarryExclusionsAndAreSharedByProjectProcesses()
        {
            var sourceRoot = TestPath.Combine(PathTable, "sources");
            var settings = new NinjaResolverSettings
            {
                SourceDirectories = new[] { new NinjaSourceDirectorySettings
                {
                    Root = sourceRoot,
                    ExcludedPaths = new[] { RelativePath.Create(PathTable.StringTable, "private/state") },
                    ExcludedNames = new[] { PathAtom.Create(PathTable.StringTable, ".git") },
                } },
            };
            var first = CreateNinjaNode(outputs: Paths("one.out"));
            var second = CreateNinjaNode(outputs: Paths("two.out"));
            var result = Start(settings).AddAll(first, second).ScheduleAll().AssertSuccess();
            var seal = Assert.Single(Assert.IsType<TestPipGraph>(result.PipGraph)
                .RetrieveScheduledPips().OfType<SealDirectory>());

            Assert.Equal(SealDirectoryKind.SourceAllDirectories, seal.Kind);
            Assert.Single(seal.SourceExclusions.Paths);
            Assert.Single(seal.SourceExclusions.Names);
            Assert.Contains(result.RetrieveSuccessfulProcess(first).DirectoryDependencies, directory => directory.Path == sourceRoot);
            Assert.Contains(result.RetrieveSuccessfulProcess(second).DirectoryDependencies, directory => directory.Path == sourceRoot);
            Assert.False(result.RetrieveSuccessfulProcess(first).AllowUndeclaredSourceReads);
            Assert.False(result.RetrieveSuccessfulProcess(second).AllowUndeclaredSourceReads);

            var remappedPaths = new PathTable();
            var remapped = new NinjaResolverSettings(settings, new PathRemapper(PathTable, remappedPaths));
            var source = Assert.Single(remapped.SourceDirectories);
            Assert.Equal(sourceRoot.ToString(PathTable), source.Root.ToString(remappedPaths));
            Assert.Equal("private/state", Assert.Single(source.ExcludedPaths).ToString(remappedPaths.StringTable).Replace('\\', '/'));
            Assert.Equal(".git", Assert.Single(source.ExcludedNames).ToString(remappedPaths.StringTable));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void SourceSealsReplaceLegacyGitUntracking(bool useSourceSeal)
        {
            var git = TestPath.Combine(PathTable, ".git");
            FileSystem.CreateDirectory(git);
            var settings = new NinjaResolverSettings
            {
                SourceDirectories = useSourceSeal
                    ? new[] { new NinjaSourceDirectorySettings { Root = TestPath } }
                    : null,
            };
            var node = CreateNinjaNode(outputs: Paths("one.out"));
            var process = Start(settings).Add(node).ScheduleAll().AssertSuccess().RetrieveSuccessfulProcess(node);

            Assert.Equal(!useSourceSeal, process.AllowUndeclaredSourceReads);
            Assert.Equal(!useSourceSeal, process.UntrackedScopes.Contains(git));
        }

        [Fact]
        public void CorrectScheduleGivenDependencyOrder()
        {
            var nodeA = CreateNinjaNode(rule: "ruleA", 
                command: OperatingSystemHelper.IsWindowsOS ? $@"{CMD} /C ""echo hola > hola.txt""" : $@"{BASH} -c ""echo hola > hola.txt""");
            var nodeB = CreateNinjaNode(rule: "ruleB", dependencies: new[] { nodeA });
            var nodeC = CreateNinjaNode(rule: "ruleC", dependencies: new[] { nodeA });
            var nodeD = CreateNinjaNode(rule: "ruleD", dependencies: new[] { nodeC, nodeB });

           Start().AddAll(nodeA, nodeB, nodeC, nodeD).ScheduleAll().AssertSuccess();
        }

        [Fact]
        public void CorrectInputs()
        {
            var node = CreateNinjaNode("myRule", inputs: Paths("input.o"));
            var inputs = Start().Add(node).ScheduleAll().RetrieveSuccessfulProcess(node).Dependencies;
            XAssert.IsTrue(inputs.Any(file => GetFileName(file) == "input.o"));
        }

        [Fact]
        public void DoNotSchedulePhonyProcess()
        {
            var trueNode = CreateNinjaNode(outputs: Paths("foo.out"));
            var phonyNode = CreateNinjaNode(rule: "phony", command: "", inputs: Paths("foo.out"));

            var process = Start().AddAll(trueNode, phonyNode).ScheduleAll().RetrieveSuccessfulProcess(phonyNode);
            XAssert.IsNull(process);
        }

        [Fact]
        public void AdditionalOutputDirectoriesAreRemapped()
        {
            var oldPaths = new PathTable();
            var newPaths = new PathTable();
            AbsolutePath.Create(newPaths, Path.GetFullPath("unrelated"));
            var absolute = Path.GetFullPath("generated");
            var relative = Path.Combine("extra", "generated");
            var settings = new NinjaResolverSettings
            {
                AdditionalOutputDirectories = new[]
                {
                    new DiscriminatingUnion<AbsolutePath, RelativePath>(AbsolutePath.Create(oldPaths, absolute)),
                    new DiscriminatingUnion<AbsolutePath, RelativePath>(RelativePath.Create(oldPaths.StringTable, relative)),
                },
            };

            var remapped = new NinjaResolverSettings(settings, new PathRemapper(oldPaths, newPaths));

            XAssert.AreEqual(AbsolutePath.Create(newPaths, absolute),
                Assert.IsType<AbsolutePath>(remapped.AdditionalOutputDirectories[0].GetValue()));
            XAssert.AreEqual(RelativePath.Create(newPaths.StringTable, relative),
                Assert.IsType<RelativePath>(remapped.AdditionalOutputDirectories[1].GetValue()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ProjectRootOutputHonorsSourceWritePolicy(bool writable)
        {
            var settings = new NinjaResolverSettings
            {
                AllowWritableSourceDirectory = writable,
                AdditionalOutputDirectories = new[]
                {
                    new DiscriminatingUnion<AbsolutePath, RelativePath>(TestPath.Combine(PathTable, "outputs")),
                },
            };
            var node = CreateNinjaNode(outputs: new[]
            {
                TestPath.Combine(PathTable, "outputs").Combine(PathTable, "product"),
            }.ToReadOnlySet());
            var process = Start(settings).Add(node).ScheduleAll().RetrieveSuccessfulProcess(node);

            Assert.Equal(writable, process.DirectoryOutputs.Any(directory => directory.Path == TestPath));
            Assert.Contains(process.DirectoryOutputs, directory => directory.Path == TestPath.Combine(PathTable, "outputs"));
            Assert.Equal(writable, new NinjaResolverSettings(settings, new PathRemapper(PathTable, PathTable)).AllowWritableSourceDirectory);
        }

        private NinjaProject Project(string name, params NinjaNode[] nodes) =>
            new NinjaProject(new NinjaProjectSettings
            {
                Name = name,
                Root = TestPath,
                Environment = new Dictionary<string, EnvironmentData>(),
            }, PathTable) { Nodes = nodes };

        [Fact]
        public void StitchingSortsCrossGraphDependencies()
        {
            var producer = CreateNinjaNode(rule: "producer", outputs: Paths("header.h"));
            var consumer = CreateNinjaNode(rule: "consumer", inputs: Paths("header.h"), outputs: Paths("consumer.o"));

            Assert.True(NinjaGraphStitcher.TrySort(new[] { Project("consumer", consumer), Project("producer", producer) },
                PathTable, out var sorted, out var error), error);
            Assert.Equal(new[] { producer, consumer }, sorted);
        }

        [Fact]
        public void StitchingRejectsDuplicateOutputs()
        {
            var first = CreateNinjaNode(rule: "first", outputs: Paths("header.h"));
            var second = CreateNinjaNode(rule: "second", outputs: Paths("header.h"));

            Assert.False(NinjaGraphStitcher.TrySort(new[] { Project("first-project", first), Project("second-project", second) },
                PathTable, out _, out var error));
            Assert.Contains("Duplicate Ninja producer", error);
            Assert.Contains("first-project", error);
            Assert.Contains("second-project", error);
        }

        [Fact]
        public void StitchingRejectsCrossGraphCycles()
        {
            var first = CreateNinjaNode(rule: "first", inputs: Paths("second.out"), outputs: Paths("first.out"));
            var second = CreateNinjaNode(rule: "second", inputs: Paths("first.out"), outputs: Paths("second.out"));

            Assert.False(NinjaGraphStitcher.TrySort(new[] { Project("first-project", first), Project("second-project", second) },
                PathTable, out _, out var error));
            Assert.Contains("Cycle", error);
            Assert.Contains("first-project", error);
            Assert.Contains("second-project", error);
        }

        [Fact]
        public void GroupedProjectPathsAreRemapped()
        {
            var newPaths = new PathTable();
            AbsolutePath.Create(newPaths, Path.GetFullPath("unrelated"));
            var settings = new NinjaResolverSettings
            {
                Projects = new[] { new NinjaProjectSettings
                {
                    Name = "project",
                    Root = TestPath,
                    SpecFile = TestPath.Combine(PathTable, "nested").Combine(PathTable, "build.ninja"),
                    Targets = new[] { "all" },
                    Environment = new Dictionary<string, EnvironmentData>(),
                    DirectoryExports = new[] { TestPath.Combine(PathTable, "export") },
                    DirectoryImports = new Dictionary<string, IReadOnlyList<AbsolutePath>>
                    {
                        ["producer"] = new[] { TestPath.Combine(PathTable, "import") },
                    },
                } },
            };

            var remapped = new NinjaResolverSettings(settings, new PathRemapper(PathTable, newPaths));
            Assert.Equal(TestPath.ToString(PathTable), remapped.Projects[0].Root.ToString(newPaths));
            Assert.Equal(settings.Projects[0].SpecFile.ToString(PathTable), remapped.Projects[0].SpecFile.ToString(newPaths));
            Assert.Equal("all", Assert.Single(remapped.Projects[0].Targets));
            Assert.Empty(remapped.Projects[0].Environment);
            Assert.Equal(settings.Projects[0].DirectoryExports[0].ToString(PathTable),
                Assert.Single(remapped.Projects[0].DirectoryExports).ToString(newPaths));
            Assert.Equal(settings.Projects[0].DirectoryImports["producer"][0].ToString(PathTable),
                Assert.Single(remapped.Projects[0].DirectoryImports["producer"]).ToString(newPaths));
        }

        [FactIfSupported(requiresWindowsBasedOperatingSystem: true)]
        public void CorrectSeparationOfQuotedExecutable()
        {
            var quotedBogus = $@"""{BogusExecutable}""";
            var options = "--great --options";
            var commandLine = $"{quotedBogus} {options}";

            var node = CreateNinjaNode(command: commandLine, outputs: Paths("foo.out"));
            var process = Start().Add(node).ScheduleAll().RetrieveSuccessfulProcess(node);
            XAssert.AreEqual(BogusExecutable, process.Executable.Path.ToString(PathTable));
            XAssert.AreEqual(options, process.Arguments.ToString(PathTable));
        }

        [FactIfSupported(requiresWindowsBasedOperatingSystem: true)]
        public void CorrectSeparationOfExecutable()
        {
            var options = "--great --options";
            var commandLine = $"{BogusExecutable} {options}";

            var node = CreateNinjaNode(command: commandLine, outputs: Paths("foo.out"));
            var process = Start().Add(node).ScheduleAll().RetrieveSuccessfulProcess(node);
            XAssert.AreEqual(BogusExecutable, process.Executable.Path.ToString(PathTable));
            XAssert.AreEqual(options, process.Arguments.ToString(PathTable));
        }

        [TheoryIfSupported(requiresWindowsBasedOperatingSystem: true)]
        [InlineData("/SomeArg /ZI", "/SomeArg /Z7 ")]
        [InlineData("/SomeArg /Zi", "/SomeArg /Z7 ")]
        [InlineData("/SomeArg /Zi /AnotherArg", "/SomeArg /Z7 /AnotherArg")]
        [InlineData("/SomeArg /Zi /FS /MP9 /AnotherArg", "/SomeArg /Z7 /AnotherArg")]
        [InlineData("/SomeArg /Zi /FS /AnotherArg /FS /MP9", "/SomeArg /Z7 /AnotherArg ")]
        [InlineData("/SomeArg /DEBUG /AnotherArg", "/SomeArg /DEBUG /AnotherArg")]
        public void ReplacePdbOptions(string rawArguments, string processedArguments)
        {
            // Suppress the command line editing
            try
            {
                EngineEnvironmentSettings.NinjaResolverAllowCxxDebugFlags.Value = false;
                EngineEnvironmentSettings.NinjaResolverAllowPdbOutputFlags.Value = false;

                var commandLine = $"{BogusExecutable} {rawArguments}";
                var node = CreateNinjaNode(command: commandLine, outputs: Paths("foo.out"));
                var process = Start().Add(node).ScheduleAll().RetrieveSuccessfulProcess(node);
                XAssert.AreEqual(processedArguments, process.Arguments.ToString(PathTable));

                // If these setting are true, we shouldn't edit the command line
                EngineEnvironmentSettings.NinjaResolverAllowCxxDebugFlags.Value = true;
                EngineEnvironmentSettings.NinjaResolverAllowPdbOutputFlags.Value = true;
                process = Start().Add(node).ScheduleAll().RetrieveSuccessfulProcess(node);
                XAssert.AreEqual(rawArguments, process.Arguments.ToString(PathTable));
            }
            finally
            {
                EngineEnvironmentSettings.NinjaResolverAllowCxxDebugFlags.Value = false;
                EngineEnvironmentSettings.NinjaResolverAllowPdbOutputFlags.Value = false;
            }
        }

        [Fact]
        public void SemistableHashesArePreservedForTheSameSchedule()
        {
            var nodeA = CreateNinjaNode("ruleA", outputs: Paths("fileA.out"), inputs: Paths("input.txt"));
            var nodeB = CreateNinjaNode("ruleB", outputs: Paths("fileB.out"), inputs: Paths("inputB.txt"));

            var processes = Start()
                .Add(nodeA)
                .Add(nodeB)
                .ScheduleAll()
                .AssertSuccess()
                .RetrieveAllProcesses();

            var hashes = new HashSet<string>(processes.Select(pip => pip.FormattedSemiStableHash));

            // Schedule again with the same set of specs
            processes = Start()
                .Add(nodeA)
                .Add(nodeB)
                .ScheduleAll()
                .AssertSuccess()
                .RetrieveAllProcesses();

            var hashes2 = new HashSet<string>(processes.Select(pip => pip.FormattedSemiStableHash));

            // Semistable hashes of both runs must be equivalent
            Assert.True(HashSet<string>.CreateSetComparer().Equals(hashes, hashes2));
        }


        [Fact]
        public void MspdvsrvEndpointTest()
        {
            var nodeA = CreateNinjaNode("ruleA", command: OperatingSystemHelper.IsWindowsOS ? $"{CMD} /C echo first" : $"{BASH} -c echo first", outputs: Paths("fileA.out"), inputs: Paths("input.txt"));
            var nodeB = CreateNinjaNode("ruleB", command: OperatingSystemHelper.IsWindowsOS ? $"{CMD} /C echo second" : $"{BASH} -c echo second", outputs: Paths("fileB.out"), inputs: Paths("inputB.txt"));

            var processes = Start()
                .Add(nodeA)
                .Add(nodeB)
                .ScheduleAll()
                .AssertSuccess()
                .RetrieveAllProcesses();

            // Get the _MSPDBSRV_ENDPOINT_ set for each pip
            var endpoints = processes.Select(
                pip => pip.EnvironmentVariables.First(env => 
                            (env.Name.ToString(PathTable.StringTable) == "_MSPDBSRV_ENDPOINT_") && !string.IsNullOrWhiteSpace(env.Value.ToString(PathTable))))
                      .ToList();

            // Both should have the environment variable
            Assert.Equal(2, endpoints.Count());

            // They should have different values
            Assert.NotEqual(endpoints[0], endpoints[1]);


            // Schedule again the same nodes, the endpoints should be the same
            processes = Start()
                .Add(nodeA)
                .Add(nodeB)
                .ScheduleAll()
                .AssertSuccess()
                .RetrieveAllProcesses();

            var endpoints2 = processes.Select(
                    pip => pip.EnvironmentVariables.First(env =>
                                                              (env.Name.ToString(PathTable.StringTable) == "_MSPDBSRV_ENDPOINT_") && !string.IsNullOrWhiteSpace(env.Value.ToString(PathTable))))
                .ToList();

            Assert.True(endpoints2.Contains(endpoints[0]));
            Assert.True(endpoints2.Contains(endpoints[1]));

        }

        private string GetFileName(FileArtifact file) => file.Path.GetName(PathTable).ToString(PathTable.StringTable);

        private IReadOnlySet<AbsolutePath> Paths(params string[] paths)
        {
            return paths.Select(p => TestPath.Combine(PathTable, p)).ToReadOnlySet();
        }
    }
}

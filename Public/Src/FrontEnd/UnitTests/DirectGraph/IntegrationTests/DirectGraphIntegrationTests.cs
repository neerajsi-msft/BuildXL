// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.IO;
using System.Linq;
using System.Text.Json;
using Stopwatch = System.Diagnostics.Stopwatch;
using BuildXL.Engine;
using BuildXL.Pips;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using BuildXL.Utilities.Core;
using Test.BuildXL.Processes;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;
using Test.DScript.Ast;
using Xunit;

namespace Test.BuildXL.FrontEnd.DirectGraph
{
    /// <summary>
    /// Runs the engine end to end, with the mock emitter streaming the graph to the DirectGraph resolver.
    /// </summary>
    public sealed class DirectGraphIntegrationTests : DsTestWithCacheBase
    {
        private readonly ITestOutputHelper m_output;
        // CODESYNC: Public/Src/FrontEnd/UnitTests/DirectGraph/Test.BuildXL.FrontEnd.DirectGraph.dsc
        private string PathToEmitter => Path.Combine(TestDeploymentDir, "tools", "GraphEmitter", OperatingSystemHelper.IsWindowsOS ? "GraphEmitter.exe" : "GraphEmitter");

        public DirectGraphIntegrationTests(ITestOutputHelper output)
            : base(output, usePassThroughFileSystem: true)
        {
            m_output = output;
            RegisterEventSource(global::BuildXL.Processes.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.Scheduler.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.Pips.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.FrontEnd.Core.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.FrontEnd.DirectGraph.ETWLogger.Log);
            RegisterEventSource(global::BuildXL.Engine.DirectGraph.ETWLogger.Log);
        }

        protected override bool DisableDefaultSourceResolver => true;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TextSourceSealOverlapsUseNativeGraphValidation(bool outputInsideSeal)
        {
            string output = outputInsideSeal ? "out/source/result.txt" : "out/result.txt";
            var config = Build()
                .Configuration("""
                    config({
                        resolvers: [{
                            kind: "DirectGraph", roots: ["producer"],
                            projects: [{ name: a`producer`, root: d`.`, graphFiles: [f`graph.dg`] }],
                        }],
                        mounts: [{ name: a`out`, path: p`out`, isWritable: true, isReadable: true, trackSourceFileChanges: true }],
                    });
                    """)
                .AddFile("graph.dg", $$"""
                    directgraph 3
                    path 0 {{(OperatingSystemHelper.IsWindowsOS ? CmdHelper.CmdX64 : CmdHelper.Bash)}}
                    path 1 .
                    path 2 out
                    path 3 out/source
                    path 4 {{output}}
                    process produce
                    {{"\t"}}executable p0
                    {{"\t"}}workingDirectory p1
                    {{"\t"}}outputDirectory p2 shared
                    {{"\t"}}output p4
                    sealDirectory source
                    {{"\t"}}directoryRoot p3
                    {{"\t"}}sealKind sourceAllDirectories

                    """)
                .AddFile("out/source/input.txt", "source")
                .PersistSpecsAndGetConfiguration();
            var mutable = (CommandLineConfiguration)config;
            mutable.Engine.Phase = EnginePhases.Schedule;
            mutable.DisableInBoxSdkSourceResolver = true;
            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var result = CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null,
                rememberAllChangedTrackedInputs: true, engine: out _);
            Assert.Equal(!outputInsideSeal, result.IsSuccess);
            if (outputInsideSeal)
            {
                AssertErrorEventLogged(global::BuildXL.Pips.Tracing.LogEventId.InvalidGraphSinceSourceSealedDirectoryContainsOutputFile);
            }
            Assert.False(File.Exists(Path.Combine(TestRoot, output)));
        }

        [FactIfSupported(requiresLinuxBasedOperatingSystem: true)]
        public void KatiGeneratedProjectFragmentsExecute()
        {
            string root = System.Environment.GetEnvironmentVariable("KATI_DIRECTGRAPH_FIXTURE");
            if (string.IsNullOrEmpty(root))
            {
                Assert.Skip("Set KATI_DIRECTGRAPH_FIXTURE to the isolated Kati multi-project fixture directory to run interoperability validation.");
            }

            root = Path.GetFullPath(root);
            string inputFragment(string project) => File.Exists(Path.Combine(root, project + "-inputs.dg"))
                ? $"f`{root.Replace('\\', '/')}/{project}-inputs.dg`, " : "";
            var config = Build()
                .Configuration($$"""
                    config({
                        resolvers: [{
                            kind: "DirectGraph", roots: ["consumer"],
                            projects: [
                                { name: a`tools`, root: d`{{root.Replace('\\', '/')}}`,
                                  graphFiles: [f`{{root.Replace('\\', '/')}}/tools.dg`] },
                                { name: a`producer`, root: d`{{root.Replace('\\', '/')}}/producer`,
                                  graphFiles: [{{inputFragment("producer")}}f`{{root.Replace('\\', '/')}}/producer.dg`],
                                  dependencies: ["tools"],
                                  fileExports: [p`{{root.Replace('\\', '/')}}/producer/generated.h`,
                                                p`{{root.Replace('\\', '/')}}/producer/generated-input.txt`] },
                                { name: a`consumer`, root: d`{{root.Replace('\\', '/')}}/consumer`,
                                  graphFiles: [{{inputFragment("consumer")}}f`{{root.Replace('\\', '/')}}/consumer.dg`],
                                  dependencies: ["producer", "tools"],
                                  fileExports: [p`{{root.Replace('\\', '/')}}/consumer/consumer.out`] },
                            ],
                        }],
                        mounts: [
                            { name: a`fixture`, path: p`{{root.Replace('\\', '/')}}`,
                              trackSourceFileChanges: true, isReadable: true, isWritable: true },
                            { name: a`hostUsr`, path: p`/usr`,
                              trackSourceFileChanges: true, isReadable: true, isWritable: false },
                            { name: a`hostBin`, path: p`/bin`,
                              trackSourceFileChanges: true, isReadable: true, isWritable: false },
                        ],
                    });
                    """)
                .PersistSpecsAndGetConfiguration();
            var mutable = (CommandLineConfiguration)config;
            mutable.Engine.Phase = EnginePhases.Execute;
            mutable.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutable.DisableInBoxSdkSourceResolver = true;
            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var result = CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null,
                rememberAllChangedTrackedInputs: true, engine: out _);
            Assert.True(result.IsSuccess);
            Assert.Equal(new[] { "#define GENERATED_VALUE 37", "directgraph-input" },
                File.ReadAllLines(Path.Combine(root, "consumer", "consumer.out")));
        }

        [FactIfSupported(requiresLinuxBasedOperatingSystem: true)]
        public void ParallelProjectConstructionPreservesNativeFingerprintsAndPaths()
        {
            string producer(string name, bool automaticTemp) => $$"""
                directgraph 3
                path 0 /bin/bash
                path 1 .
                path 2 out/{{name}}.in
                path 3 out/shared/{{name}}.txt
                path 4 out/shared
                path 5 out/{{name}}-temp
                path 6 out/base.txt
                path 7 out
                string 0 {{name}}
                string 1 -c
                string 2 mkdir -p out/shared && cat out/{{name}}.in > out/shared/{{name}}.txt
                string 3{{"  "}}
                writeFile prepare
                {{"\t"}}destination p2
                {{"\t"}}content s0
                process work
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}input p2
                {{"\t"}}input p6
                {{"\t"}}inputDirectory p7 base/baseSeal
                {{"\t"}}arguments concat s3 escapeArguments :
                {{"\t\t"}}s1
                {{"\t\t"}}s2
                {{"\t"}}option dependsOnCurrentOs
                {{"\t"}}output p3
                {{"\t"}}outputDirectory p4 shared
                {{"\t"}}{{(automaticTemp ? "enableTempDirectory true" : "tempDirectory p5")}}
                export publish
                {{"\t"}}file p3
                {{"\t"}}directory p4 work
                export again
                {{"\t"}}file p3
                {{"\t"}}directory p4 work

                """;
            var config = Build()
                .Configuration("""
                    config({
                        resolvers: [{
                            kind: "DirectGraph", roots: ["top", "independent"],
                            projects: [
                                { name: a`base`, root: d`.`, graphFiles: [f`base.dg`],
                                  fileExports: [p`out/base.txt`], directoryExports: [p`out`] },
                                { name: a`left`, root: d`.`, graphFiles: [f`left.dg`], dependencies: ["base"],
                                  fileExports: [p`out/shared/left.txt`] },
                                { name: a`right`, root: d`.`, graphFiles: [f`right.dg`], dependencies: ["base"],
                                  fileExports: [p`out/shared/right.txt`] },
                                { name: a`independent`, root: d`.`, graphFiles: [f`independent.dg`] },
                                { name: a`join`, root: d`.`, graphFiles: [f`join.dg`],
                                  dependencies: ["left", "right"], fileExports: [p`out/join.txt`] },
                                { name: a`top`, root: d`.`, graphFiles: [f`top.dg`],
                                  dependencies: ["join", "left", "right"] },
                            ],
                        }],
                        mounts: [{ name: a`out`, path: p`out`, isWritable: true, isReadable: true, trackSourceFileChanges: true }],
                    });
                    """)
                .AddFile("base.dg", $$"""
                    directgraph 3
                    path 0 out/base.txt
                    path 1 out
                    string 0 base
                    writeFile prepare
                    {{"\t"}}destination p0
                    {{"\t"}}content s0
                    sealDirectory baseSeal
                    {{"\t"}}directoryRoot p1
                    {{"\t"}}content p0
                    export publish
                    {{"\t"}}file p0
                    {{"\t"}}directory p1

                    """)
                .AddFile("left.dg", producer("left", automaticTemp: true))
                .AddFile("right.dg", producer("right", automaticTemp: false))
                .AddFile("independent.dg", $$"""
                    directgraph 3
                    path 0 /bin/bash
                    path 1 .
                    path 2 out/independent.txt
                    string 0 -c
                    string 1 printf independent > out/independent.txt
                    string 2{{"  "}}
                    process work
                    {{"\t"}}executable p0
                    {{"\t"}}workingDirectory p1
                    {{"\t"}}arguments concat s2 escapeArguments :
                    {{"\t\t"}}s0
                    {{"\t\t"}}s1
                    {{"\t"}}option dependsOnCurrentOs
                    {{"\t"}}enableTempDirectory true
                    {{"\t"}}output p2

                    """)
                .AddFile("join.dg", $$"""
                    directgraph 3
                    path 0 out/shared
                    path 1 out/shared/left.txt
                    path 2 out/shared/right.txt
                    path 3 /bin/bash
                    path 4 .
                    path 5 out/join.txt
                    string 0 -c
                    string 1 cat out/shared/left.txt out/shared/right.txt > out/join.txt
                    composeSharedOpaque combined
                    {{"\t"}}directoryRoot p0
                    {{"\t"}}directory p0 left/work
                    {{"\t"}}directory p0 right/work
                    string 2{{"  "}}
                    process work
                    {{"\t"}}executable p3
                    {{"\t"}}workingDirectory p4
                    {{"\t"}}input p1
                    {{"\t"}}input p2
                    {{"\t"}}inputDirectory p0 combined
                    {{"\t"}}arguments concat s2 escapeArguments :
                    {{"\t\t"}}s0
                    {{"\t\t"}}s1
                    {{"\t"}}option dependsOnCurrentOs
                    {{"\t"}}enableTempDirectory true
                    {{"\t"}}output p5
                    export publish
                    {{"\t"}}file p5
                    {{"\t"}}directory p0 combined
                    export again
                    {{"\t"}}file p5
                    {{"\t"}}directory p0 combined

                    """)
                .AddFile("top.dg", $$"""
                    directgraph 3
                    path 0 /bin/bash
                    path 1 .
                    path 2 out/join.txt
                    path 3 out/top.txt
                    path 4 out/shared
                    string 0 -c
                    string 1 cat out/join.txt > out/top.txt
                    string 2{{"  "}}
                    process work
                    {{"\t"}}executable p0
                    {{"\t"}}workingDirectory p1
                    {{"\t"}}input p2
                    {{"\t"}}inputDirectory p4 join/combined
                    {{"\t"}}arguments concat s2 escapeArguments :
                    {{"\t\t"}}s0
                    {{"\t\t"}}s1
                    {{"\t"}}option dependsOnCurrentOs
                    {{"\t"}}enableTempDirectory true
                    {{"\t"}}output p3

                    """)
                .PersistSpecsAndGetConfiguration();
            var mutable = (CommandLineConfiguration)config;
            mutable.Engine.Phase = EnginePhases.Execute;
            mutable.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutable.DisableInBoxSdkSourceResolver = true;
            mutable.Cache.CacheGraph = false;
            mutable.Cache.AllowFetchingCachedGraphFromContentCache = false;
            mutable.Schedule.ComputePipStaticFingerprints = true;
            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var deployment = CreateAppDeployment(tempFiles);
            string[] baseline = null;
            foreach (int concurrency in new[] { 1, 4, 4, 4 })
            {
                mutable.FrontEnd.MaxFrontEndConcurrency = concurrency;
                var elapsed = Stopwatch.StartNew();
                var result = CreateAndRunEngine(config, deployment, testRootDirectory: null,
                    rememberAllChangedTrackedInputs: true, engine: out _);
                m_output.WriteLine($"DirectGraph six-project cold construction /maxFrontEndConcurrency:{concurrency}, whole engine run: {elapsed.ElapsedMilliseconds}ms");
                Assert.True(result.IsSuccess);
                var graph = result.EngineState.PipGraph;
                var fingerprints = graph.AllPipStaticFingerprints.ToDictionary(pair => pair.Key, pair => pair.Value.ToString());
                string path(AbsolutePath value) => value.IsValid ? value.ToString(PathTable) : "";
                var pips = new[] { PipType.Process, PipType.WriteFile, PipType.SealDirectory, PipType.CopyFile }
                    .SelectMany(type => graph.RetrievePipsOfType(type)).ToArray();
                var snapshot = pips.Select(pip =>
                {
                    Assert.True(fingerprints.ContainsKey(pip.PipId));
                    string products = pip switch
                    {
                        Process process => string.Join(";", process.FileOutputs.Select(file => path(file.Path)).OrderBy(value => value)) +
                            "|" + string.Join(";", process.DirectoryOutputs.Select(directory => path(directory.Path)).OrderBy(value => value)) +
                            "|" + path(process.TempDirectory) + "|" + path(process.StandardDirectory),
                        WriteFile write => path(write.Destination.Path),
                        CopyFile copy => path(copy.Destination.Path),
                        SealDirectory seal => path(seal.DirectoryRoot) + "|" + seal.Kind,
                        _ => throw new System.InvalidOperationException(),
                    };
                    return $"{pip.PipType}|{pip.SemiStableHash:X16}|{fingerprints[pip.PipId]}|{products}";
                }).OrderBy(value => value, System.StringComparer.Ordinal).ToArray();
                Assert.Equal(5, pips.OfType<Process>().Count());
                Assert.Contains(pips.OfType<SealDirectory>(), seal => seal.IsComposite);
                Assert.All(pips.OfType<Process>(), process => Assert.True(process.TempDirectory.IsValid));
                if (baseline == null)
                {
                    baseline = snapshot;
                }
                else
                {
                    Assert.Equal(baseline, snapshot);
                    Assert.Equal(5, result.EnginePerformanceInfo.SchedulerPerformanceInfo.ProcessPipCacheHits);
                }
                Assert.Equal("leftright", File.ReadAllText(Path.Combine(TestRoot, "out", "top.txt")));
            }
        }

        [Fact]
        public void ProjectFragmentsExecuteAndExposeRewrittenExportsToDScript()
        {
            string[] rewrite = OperatingSystemHelper.IsWindowsOS
                ? new[] { CmdHelper.CmdX64, "/d", "/c", @"echo world>> out\a\a.txt" }
                : new[] { CmdHelper.Bash, "-c", "echo world >> out/a/a.txt" };
            string strings = string.Join("\n", rewrite.Skip(1).Select((argument, index) =>
                $"string {index + 1} " + argument.Replace("\n", "\n\t"))) + $"\nstring {rewrite.Length}  ";
            string arguments = $"\targuments concat s{rewrite.Length} escapeArguments :\n" +
                string.Join("\n", rewrite.Skip(1).Select((_, index) => $"\t\ts{index + 1}"));
            var config = Build()
                .Configuration($$"""
                    config({
                        resolvers: [
                            {
                                kind: "DirectGraph",
                                roots: ["b"],
                                projects: [
                                    { name: a`tools`, root: d`.`, graphFiles: [f`tools.dg`],
                                      directoryExports: [p`{{Path.GetDirectoryName(rewrite[0]).Replace('\\', '/')}}`] },
                                    { name: a`a`, root: d`.`, graphFiles: [f`a.dg`], policyFiles: [f`a.policy.dg`],
                                      dependencies: ["tools"], directoryExports: [p`out/a`] },
                                    { name: a`b`, root: d`.`, graphFiles: [f`b.dg`],
                                      dependencies: ["a"], fileExports: [p`out/b.txt`] },
                                ],
                            },
                            { kind: "DScript", modules: [{ moduleName: "consumer", projects: [f`consumer.dsc`] }] },
                        ],
                        mounts: [{ name: a`out`, path: p`out`, trackSourceFileChanges: true, isReadable: true, isWritable: true }],
                    });
                    """)
                .AddFile("tools.dg", $$"""
                    directgraph 3
                    path 0 {{Path.GetDirectoryName(rewrite[0])}}
                    sealDirectory host
                    {{"\t"}}directoryRoot p0
                    {{"\t"}}sealKind sourceTopDirectoryOnly

                    """)
                .AddFile("a.dg", $$"""
                    directgraph 3
                    path 0 out/a/a.txt
                    path 1 {{rewrite[0]}}
                    path 2 .
                    string 0 hello
                    {{"\t"}}
                    {{strings}}
                    writeFile write
                    {{"\t"}}destination p0
                    {{"\t"}}content s0
                    process rewrite
                    {{"\t"}}executable p1
                    {{"\t"}}workingDirectory p2
                    {{arguments}}
                    {{"\t"}}option dependsOnCurrentOs
                    {{"\t"}}rewrite p0

                    """)
                .AddFile("a.policy.dg", $$"""
                    directgraph 3
                    path 0 out/a
                    path 1 out/a/a.txt
                    sealDirectory publicHeaders
                    {{"\t"}}directoryRoot p0
                    {{"\t"}}sealKind partial
                    {{"\t"}}content p1

                    """)
                .AddFile("b.dg", $$"""
                    directgraph 3
                    path 0 out/a/a.txt
                    path 1 out/b.txt
                    copyFile write
                    {{"\t"}}source p0
                    {{"\t"}}destination p1

                    """)
                .AddSpec("consumer.dsc", """
                    import { files } from "b";
                    const exported = files.get(p`out/b.txt`);
                    const exists = Contract.assert(exported !== undefined);
                    @@public
                    export const artifact = exported;
                    """)
                .PersistSpecsAndGetConfiguration();
            var mutable = (CommandLineConfiguration)config;
            mutable.Engine.Phase = EnginePhases.Execute;
            mutable.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutable.DisableInBoxSdkSourceResolver = true;
            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var result = CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null,
                rememberAllChangedTrackedInputs: true, engine: out _);
            Assert.True(result.IsSuccess);
            Assert.Equal(new[] { "hello", "world" }, File.ReadAllLines(Path.Combine(TestRoot, "out", "b.txt")));
        }

        [Fact]
        public void EmittedPipsRunAndProduceTheirOutputs()
        {
            string[] command = OperatingSystemHelper.IsWindowsOS
                ? new[] { CmdHelper.CmdX64, "/d", "/c", @"type out\input.txt > out\opaque\result.txt" }
                : new[] { CmdHelper.Bash, "-c", "cat out/input.txt > out/opaque/result.txt" };

            // Appends to the file the first operation wrote, in place.
            string[] rewriteCommand = OperatingSystemHelper.IsWindowsOS
                ? new[] { CmdHelper.CmdX64, "/d", "/c", @"echo world>> out\input.txt" }
                : new[] { CmdHelper.Bash, "-c", "echo world >> out/input.txt" };

            var config = Build()
                .Configuration($$"""
                    config({
                        resolvers: [
                            {
                                kind: "DirectGraph",
                                moduleNames: [a`producer`, a`bystander`],
                                workingDirectory: d`.`,
                                tool: f`{{PathToEmitter.Replace('\\', '/')}}`,
                                arguments: ["graph.json"],
                                // More than the timer behind it takes, so the resolver has to cap it.
                                idleTimeoutMin: 100000,
                            },
                        ],
                        mounts: [{ name: a`out`, path: p`out`, trackSourceFileChanges: true, isReadable: true, isWritable: true }],
                        // Module evaluation runs under this qualifier, which the emitted pips must not depend on.
                        qualifiers: { defaultQualifier: { configuration: "debug" } },
                    });
                    """)
                .AddFile("graph.json", $$"""
                    { "modules": [ { "name": "producer", "operations": [
                        { "name": "write", "kind": "writeFile", "destination": "out/input.txt", "content": "hello\n" },
                        { "name": "rewrite", "kind": "process", "executable": {{JsonSerializer.Serialize(rewriteCommand[0])}}, "workingDirectory": ".",
                          "arguments": {{JsonSerializer.Serialize(rewriteCommand[1..])}}, "options": [ "dependsOnCurrentOs" ],
                          "rewrittenFiles": [ { "producedBy": "write", "path": "out/input.txt" } ] },
                        { "name": "run", "kind": "process", "executable": {{JsonSerializer.Serialize(command[0])}}, "workingDirectory": ".",
                          "arguments": {{JsonSerializer.Serialize(command[1..])}}, "options": [ "dependsOnCurrentOs" ],
                          "inputFiles": [ { "producedBy": "rewrite", "path": "out/input.txt" } ], "producesDirectories": [ "out/opaque" ] }
                    ] }, { "name": "bystander", "operations": [
                        { "name": "bystanderWrite", "kind": "writeFile", "destination": "out/bystander.txt", "content": "not requested" }
                    ] } ] }
                    """)
                .PersistSpecsAndGetConfiguration();

            var mutableConfig = (CommandLineConfiguration)config;
            mutableConfig.Engine.Phase = EnginePhases.Execute;
            mutableConfig.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutableConfig.DisableInBoxSdkSourceResolver = true;

            // The emitter produces the pips of both modules, whatever the filter selects.
            mutableConfig.Filter = "module='producer'";

            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var result = CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null, rememberAllChangedTrackedInputs: true, engine: out var engine);

            Assert.True(result.IsSuccess);
            Assert.Equal(100000, engine.Configuration.Resolvers.OfType<IDirectGraphResolverSettings>().Single().IdleTimeoutMin);
            // The consumer reads the rewritten version, which still has what the first operation wrote.
            Assert.Equal(new[] { "hello", "world" }, File.ReadAllLines(Path.Combine(TestRoot, "out", "opaque", "result.txt")));
            Assert.False(File.Exists(Path.Combine(TestRoot, "out", "bystander.txt")));
        }

        [Fact]
        public void DScriptConsumesCompletedExportMapsWithDirectoryKindsAndContents()
        {
            string[] command = OperatingSystemHelper.IsWindowsOS
                ? new[] { CmdHelper.CmdX64, "/d", "/c", @"type out\partial\input.txt > out\opaque\result.txt" }
                : new[] { CmdHelper.Bash, "-c", "cat out/partial/input.txt > out/opaque/result.txt" };

            var config = Build()
                .Configuration($$"""
                    config({
                        resolvers: [
                            {
                                kind: "DirectGraph",
                                moduleNames: [a`producer`, a`other`, a`empty`],
                                workingDirectory: d`.`,
                                tool: f`{{PathToEmitter.Replace('\\', '/')}}`,
                                arguments: ["graph.json"],
                            },
                            {
                                kind: "DScript",
                                modules: [{ moduleName: "consumer", projects: [f`consumer.dsc`] }],
                            },
                        ],
                        mounts: [{ name: a`out`, path: p`out`, trackSourceFileChanges: true, isReadable: true, isWritable: true }],
                        qualifiers: { defaultQualifier: { configuration: "debug" } },
                    });
                    """)
                .AddFile("graph.json", $$"""
                    { "modules": [ { "name": "producer", "operations": [
                        { "name": "write", "kind": "writeFile", "destination": "out/partial/input.txt", "content": "exported" },
                        { "name": "writeFull", "kind": "writeFile", "destination": "out/full/input.txt", "content": "full" },
                        { "name": "run", "kind": "process", "executable": {{JsonSerializer.Serialize(command[0])}}, "workingDirectory": ".",
                          "arguments": {{JsonSerializer.Serialize(command[1..])}}, "options": [ "dependsOnCurrentOs" ],
                          "inputFiles": [ { "producedBy": "write", "path": "out/partial/input.txt" } ], "producesDirectories": [ "out/opaque" ] },
                        { "name": "partial", "kind": "sealDirectory", "directoryRoot": "out/partial", "sealKind": "partial",
                          "contents": [ { "producedBy": "write", "path": "out/partial/input.txt" } ] },
                        { "name": "full", "kind": "sealDirectory", "directoryRoot": "out/full", "sealKind": "full",
                          "contents": [ { "producedBy": "writeFull", "path": "out/full/input.txt" } ] },
                        { "name": "sourceAll", "kind": "sealDirectory", "directoryRoot": "sourceAll", "sealKind": "sourceAllDirectories" },
                        { "name": "sourceTop", "kind": "sealDirectory", "directoryRoot": "sourceTop", "sealKind": "sourceTopDirectoryOnly" },
                        { "name": "barrier", "kind": "composeSharedOpaque", "directoryRoot": "out", "contents": [ { "producedBy": "run" } ] },
                        { "name": "exports", "kind": "export",
                          "files": [ { "producedBy": "write", "path": "out/partial/input.txt" } ],
                          "directories": [
                            { "producedBy": "run" }, { "producedBy": "partial" }, { "producedBy": "full" },
                            { "producedBy": "sourceAll" }, { "producedBy": "sourceTop" }, { "producedBy": "barrier" } ] }
                    ] }, { "name": "other", "operations": [
                        { "name": "exportAgain", "kind": "export", "files": [ { "producedBy": "write", "path": "out/partial/input.txt" } ] }
                    ] } ] }
                    """)
                .AddFile("sourceAll/input.txt", "source")
                .AddFile("sourceTop/input.txt", "top")
                .AddSpec("consumer.dsc", """
                    import { files, directories } from "producer";
                    import { files as otherFiles, directories as otherDirectories } from "other";
                    import { files as emptyFiles, directories as emptyDirectories } from "empty";

                    const file = files.get(p`out/partial/input.txt`);
                    const exportedFileExists = Contract.assert(file !== undefined, "The required DirectGraph file was not exported");
                    const onlySelectedArtifacts = Contract.assert(files.count() === 1 && files.get(p`out/full/input.txt`) === undefined);
                    const missingKeys = Contract.assert(files.get(p`missing`) === undefined && directories.get(p`missing`) === undefined);
                    const independentModules = Contract.assert(otherFiles.get(file.path).path === file.path && otherDirectories.count() === 0);
                    const emptyMaps = Contract.assert(emptyFiles.count() === 0 && emptyDirectories.count() === 0);

                    const partial = directories.get(p`out/partial`);
                    const full = directories.get(p`out/full`);
                    const opaque = directories.get(p`out/opaque`);
                    const kinds = Contract.assert(
                        partial.kind === "partial" && full.kind === "full" && opaque.kind === "shared"
                        && directories.get(p`sourceAll`).kind === "sourceAllDirectories"
                        && directories.get(p`sourceTop`).kind === "sourceTopDirectories"
                        && directories.get(p`out`).kind === "shared");
                    const contents = Contract.assert(
                        directories.count() === 6 && partial.contents.length === 1 && full.contents.length === 1
                        && partial.getFile(r`input.txt`).path === file.path && opaque.contents.length === 0);

                    export const fromFile = _PreludeAmbientHack_Transformer.copyFile(file, p`out/from-file.txt`);
                    export const fromSeal = _PreludeAmbientHack_Transformer.copyFile(full.getFile(r`input.txt`), p`out/from-seal.txt`);
                    export const fromOpaque = _PreludeAmbientHack_Transformer.copyFile(opaque.assertExistence(r`result.txt`), p`out/from-opaque.txt`);
                    """)
                .PersistSpecsAndGetConfiguration();

            var mutableConfig = (CommandLineConfiguration)config;
            mutableConfig.Engine.Phase = EnginePhases.Execute;
            mutableConfig.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutableConfig.DisableInBoxSdkSourceResolver = true;
            mutableConfig.Filter = "module='consumer'";

            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var result = CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null, rememberAllChangedTrackedInputs: true, engine: out _);

            Assert.True(result.IsSuccess);
            Assert.Equal("exported", File.ReadAllText(Path.Combine(TestRoot, "out", "from-file.txt")));
            Assert.Equal("full", File.ReadAllText(Path.Combine(TestRoot, "out", "from-seal.txt")));
            Assert.Equal("exported", File.ReadAllText(Path.Combine(TestRoot, "out", "from-opaque.txt")));
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, true, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, false, false)]
        [InlineData(true, true, false)]
        public void DuplicateExportsMergeOnlyIdenticalArtifacts(bool firstIsDirectory, bool secondIsDirectory, bool sameMessage)
        {
            string exportOperation(string name, params bool[] directoryKinds) => JsonSerializer.Serialize(new
            {
                name,
                kind = "export",
                files = directoryKinds.Where(directory => !directory).Select(_ => new { sourcePath = "out/key" }).ToArray(),
                directories = directoryKinds.Where(directory => directory).Select(_ => new { producedBy = "seal", path = "out/key" }).ToArray(),
            });

            string exports = sameMessage
                ? exportOperation("batch", firstIsDirectory, secondIsDirectory)
                : exportOperation("first", firstIsDirectory) + "," + exportOperation("second", secondIsDirectory);

            var config = Build()
                .Configuration($$"""
                    config({
                        resolvers: [{{Resolver("producer", "graph.json")}}],
                        mounts: [{ name: a`out`, path: p`out`, trackSourceFileChanges: true, isReadable: true, isWritable: true }],
                    });
                    """)
                .AddFile("graph.json", $$"""
                    { "modules": [ { "name": "producer", "operations": [
                        { "name": "seal", "kind": "sealDirectory", "directoryRoot": "out/key", "sealKind": "partial" },
                        {{exports}}
                    ] } ] }
                    """)
                .PersistSpecsAndGetConfiguration();

            var mutableConfig = (CommandLineConfiguration)config;
            mutableConfig.Engine.Phase = EnginePhases.Execute;
            mutableConfig.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutableConfig.DisableInBoxSdkSourceResolver = true;

            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var result = CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null, rememberAllChangedTrackedInputs: true, engine: out _);

            if (firstIsDirectory == secondIsDirectory)
            {
                Assert.True(result.IsSuccess);
                return;
            }
            Assert.False(result.IsSuccess);
            AssertErrorEventLogged(global::BuildXL.Engine.DirectGraph.Tracing.LogEventId.GraphOperationRejected);
            Assert.Contains(
                EventListener.GetLogMessagesForEventId((int)global::BuildXL.Engine.DirectGraph.Tracing.LogEventId.GraphOperationRejected),
                message => message.Contains("already exports"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExternalExportsRejectDifferentArtifactIdentitiesAtTheSamePath(bool directory)
        {
            string declarations = directory ? """
                { "name": "first", "kind": "sealDirectory", "directoryRoot": "src", "sealKind": "sourceAllDirectories" },
                { "name": "second", "kind": "sealDirectory", "directoryRoot": "src", "sealKind": "sourceTopDirectoryOnly" }
                """ : $$"""
                { "name": "first", "kind": "writeFile", "destination": "out/key", "content": "first" },
                { "name": "second", "kind": "process", "executable": {{JsonSerializer.Serialize(OperatingSystemHelper.IsWindowsOS ? CmdHelper.CmdX64 : CmdHelper.Bash)}},
                  "workingDirectory": ".", "options": ["dependsOnCurrentOs"],
                  "rewrittenFiles": [{ "producedBy": "first", "path": "out/key" }] }
                """;
            string field = directory ? "directories" : "files";
            string path = directory ? "src" : "out/key";
            var config = Build()
                .Configuration($$"""
                    config({
                        resolvers: [{{Resolver("producer", "graph.json")}}],
                        mounts: [{ name: a`out`, path: p`out`, trackSourceFileChanges: true, isReadable: true, isWritable: true }],
                    });
                    """)
                .AddFile("graph.json", $$"""
                    { "modules": [{ "name": "producer", "operations": [
                        {{declarations}},
                        { "name": "publishFirst", "kind": "export", "{{field}}": [{ "producedBy": "first", "path": "{{path}}" }] },
                        { "name": "publishSecond", "kind": "export", "{{field}}": [{ "producedBy": "second", "path": "{{path}}" }] }
                    ] }] }
                    """)
                .AddFile("src/input.txt", "source")
                .PersistSpecsAndGetConfiguration();
            var mutableConfig = (CommandLineConfiguration)config;
            mutableConfig.Engine.Phase = EnginePhases.Execute;
            mutableConfig.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutableConfig.DisableInBoxSdkSourceResolver = true;
            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var result = CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null,
                rememberAllChangedTrackedInputs: true, engine: out _);
            Assert.False(result.IsSuccess);
            AssertErrorEventLogged(global::BuildXL.Engine.DirectGraph.Tracing.LogEventId.GraphOperationRejected);
            Assert.Contains(EventListener.GetLogMessagesForEventId((int)global::BuildXL.Engine.DirectGraph.Tracing.LogEventId.GraphOperationRejected),
                message => message.Contains("conflicting artifact identity"));
        }

        [Fact]
        public void TwoResolversEmitTheirOwnPips()
        {
            var result = RunWithTwoResolvers("first", "second");

            Assert.True(result.IsSuccess);
            Assert.Equal("first", File.ReadAllText(Path.Combine(TestRoot, "out", "first.txt")));
            Assert.Equal("second", File.ReadAllText(Path.Combine(TestRoot, "out", "second.txt")));
        }

        [Fact]
        public void ModuleNameDeclaredByTwoResolversBelongsToTheFirst()
        {
            // As for every resolver kind, the engine keeps the first module of a name, so the second resolver owns no
            // module and none of its pips is built.
            var result = RunWithTwoResolvers("shared", "shared");

            Assert.True(result.IsSuccess);
            Assert.Equal("first", File.ReadAllText(Path.Combine(TestRoot, "out", "first.txt")));
            Assert.False(File.Exists(Path.Combine(TestRoot, "out", "second.txt")));
        }

        [Theory]
        [InlineData("exclusive")]
        [InlineData("shared")]
        [InlineData("composite")]
        public void UnlistedOpaqueMembersExecuteThroughDirectoryDependencies(string directoryKind)
        {
            bool windows = OperatingSystemHelper.IsWindowsOS;
            string executable = windows ? CmdHelper.CmdX64 : CmdHelper.Bash;
            string[] flags = windows ? new[] { "/d", "/c" } : new[] { "-c" };
            string registrations = "string 0  \n" + string.Join("\n", flags.Select((flag, index) =>
                $"string {index + 1} " + flag.Replace("\n", "\n\t")));
            string arguments(int commandId) => "\targuments concat s0 escapeArguments :\n" +
                string.Join("\n", flags.Select((_, index) => $"\t\ts{index + 1}")) + $"\n\t\ts{commandId}";
            string first = windows
                ? @"if not exist out\opaque mkdir out\opaque & echo first> out\opaque\a.txt"
                : "mkdir -p out/opaque && printf first > out/opaque/a.txt";
            string second = windows
                ? @"if not exist out\opaque mkdir out\opaque & echo second> out\opaque\b.txt"
                : "mkdir -p out/opaque && printf second > out/opaque/b.txt";
            string read = windows ? @"type out\opaque\a.txt > out\result.txt" : "cat out/opaque/a.txt > out/result.txt";
            string composition = directoryKind == "composite" ? $$"""
                process second
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{arguments(flags.Length + 2)}}
                {{"\t"}}option dependsOnCurrentOs
                {{"\t"}}outputDirectory p2 shared
                composeSharedOpaque combined
                {{"\t"}}directoryRoot p2
                {{"\t"}}directory p2 first
                {{"\t"}}directory p2 second

                """ : "";

            var config = Build()
                .Configuration($$"""
                    config({
                        resolvers: [{
                            kind: "DirectGraph", roots: ["consumer"],
                            projects: [
                                { name: a`tools`, root: d`.`, graphFiles: [f`tools.dg`],
                                  directoryExports: [p`{{Path.GetDirectoryName(executable).Replace('\\', '/')}}`] },
                                { name: a`producer`, root: d`.`, graphFiles: [f`producer.dg`], dependencies: ["tools"] },
                                { name: a`consumer`, root: d`.`, graphFiles: [f`consumer.dg`], dependencies: ["producer", "tools"] },
                            ],
                        }],
                        mounts: [{ name: a`out`, path: p`out`, trackSourceFileChanges: true, isReadable: true, isWritable: true }],
                    });
                    """)
                .AddFile("tools.dg", $$"""
                    directgraph 3
                    path 0 {{Path.GetDirectoryName(executable)}}
                    sealDirectory host
                    {{"\t"}}directoryRoot p0
                    {{"\t"}}sealKind sourceTopDirectoryOnly

                    """)
                .AddFile("producer.dg", $$"""
                    directgraph 3
                    path 0 {{executable}}
                    path 1 .
                    path 2 out/opaque
                    {{registrations}}
                    string {{flags.Length + 1}} {{first}}
                    string {{flags.Length + 2}} {{second}}
                    process first
                    {{"\t"}}executable p0
                    {{"\t"}}workingDirectory p1
                    {{arguments(flags.Length + 1)}}
                    {{"\t"}}option dependsOnCurrentOs
                    {{"\t"}}outputDirectory p2 {{(directoryKind == "exclusive" ? "exclusive" : "shared")}}
                    {{composition}}export public
                    {{"\t"}}directory p2 {{(directoryKind == "composite" ? "combined" : "first")}}

                    """)
                .AddFile("consumer.dg", $$"""
                    directgraph 3
                    path 0 {{executable}}
                    path 1 .
                    path 2 out/opaque/a.txt
                    path 3 out/result.txt
                    {{registrations}}
                    string {{flags.Length + 1}} {{read}}
                    process consume
                    {{"\t"}}executable p0
                    {{"\t"}}workingDirectory p1
                    {{arguments(flags.Length + 1)}}
                    {{"\t"}}option dependsOnCurrentOs
                    {{"\t"}}input p2
                    {{"\t"}}output p3

                    """)
                .PersistSpecsAndGetConfiguration();
            var mutable = (CommandLineConfiguration)config;
            mutable.Engine.Phase = EnginePhases.Execute;
            mutable.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutable.DisableInBoxSdkSourceResolver = true;
            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            var result = CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null,
                rememberAllChangedTrackedInputs: true, engine: out _);
            Assert.True(result.IsSuccess);
            Assert.Equal("first", File.ReadAllText(Path.Combine(TestRoot, "out", "result.txt")).Trim());
        }

        private string Resolver(string module, string graphFile) => $$"""
            {
                kind: "DirectGraph",
                moduleNames: [a`{{module}}`],
                workingDirectory: d`.`,
                tool: f`{{PathToEmitter.Replace('\\', '/')}}`,
                arguments: ["{{graphFile}}"],
            }
            """;

        // The same operation name in both graphs: operation ids belong to a resolver's own stream.
        private static string Graph(string module, string output) => $$"""
            { "modules": [ { "name": "{{module}}", "operations": [
                { "name": "write", "kind": "writeFile", "destination": "out/{{output}}.txt", "content": "{{output}}" }
            ] } ] }
            """;

        private BuildXLEngineResult RunWithTwoResolvers(string firstModule, string secondModule)
        {
            var config = Build()
                .Configuration($$"""
                    config({
                        resolvers: [
                            {{Resolver(firstModule, "first.json")}},
                            {{Resolver(secondModule, "second.json")}},
                        ],
                        mounts: [{ name: a`out`, path: p`out`, trackSourceFileChanges: true, isReadable: true, isWritable: true }],
                    });
                    """)
                .AddFile("first.json", Graph(firstModule, "first"))
                .AddFile("second.json", Graph(secondModule, "second"))
                .PersistSpecsAndGetConfiguration();

            var mutableConfig = (CommandLineConfiguration)config;
            mutableConfig.Engine.Phase = EnginePhases.Execute;
            mutableConfig.Sandbox.FileSystemMode = FileSystemMode.RealAndMinimalPipGraph;
            mutableConfig.DisableInBoxSdkSourceResolver = true;

            using var tempFiles = new TempFileStorage(canGetFileNames: true, rootPath: TestOutputDirectory);
            return CreateAndRunEngine(config, CreateAppDeployment(tempFiles), testRootDirectory: null, rememberAllChangedTrackedInputs: true, engine: out _);
        }
    }
}
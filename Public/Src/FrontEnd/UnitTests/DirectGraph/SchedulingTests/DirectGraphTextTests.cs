// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Engine.DirectGraph;
using BuildXL.FrontEnd.DirectGraph;
using BuildXL.FrontEnd.Utilities.GenericProjectGraphResolver;
using BuildXL.Pips;
using BuildXL.Pips.Builders;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Instrumentation.Common;
using GraphRenderer;
using Test.BuildXL.TestUtilities;
using Xunit;

namespace Test.BuildXL.FrontEnd.DirectGraph
{
    public sealed class DirectGraphTextTests
    {
        private const string RenderFragment = "directgraph 3\n" +
            "path 0 out/a b\n" +
            "path 1 out/dir\n" +
            "path 2 out/result\n" +
            "string 0 -c\n" +
            "string 1 echo \n" +
            "string 2 'literal'\r\n" +
            "\t\n" +
            "\tlast\rtail\n" +
            "string 3 \n" +
            "string 4 \n" +
            "\t\n" +
            "set 0\n" +
            "\tp0\n" +
            "\tp0\n" +
            "string 5  \n" +
            "writeFile script\n" +
            "\tdestination p0\n" +
            "\tcontent concat - s1 s2 s4\n" +
            "process run\n" +
            "\texecutable p0\n" +
            "\targuments concat s5 escapeArguments :\n" +
            "\t\ts0\n" +
            "\t\tconcat - s1 s2\n" +
            "\t\ts3\n" +
            "\tinput @0\n" +
            "\toutput p2\n" +
            "sealDirectory dir\n" +
            "\tdirectoryRoot p1\n" +
            "\tcontent p0 p2\n";

        private readonly BuildXLContext m_context = BuildXLContext.CreateInstanceForTesting();
        private PathTable m_paths => m_context.PathTable;
        private readonly TestEnv.TestPipGraph m_pipGraph = new();
        private readonly Dictionary<(string Module, string Symbol), PipConstructionHelper> m_helpers = new();
        private IReadOnlyDictionary<DirectGraphProject, ProjectGraphOutputs> m_outputs;
        private readonly string m_root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "directgraph-text-tests"));
        private readonly Dictionary<AbsolutePath, string> m_text = new();
        private readonly List<CapturedOperation> m_operations = new();
        private readonly List<AbsolutePath> m_opened = new();
        private sealed record CapturedOperation(DirectGraphOperation Native)
        {
            public DirectGraphProcess AddProcess => Native as DirectGraphProcess;
            public DirectGraphWriteFile AddWriteFile => Native as DirectGraphWriteFile;
            public DirectGraphCopyFile AddCopyFile => Native as DirectGraphCopyFile;
            public DirectGraphSealDirectory AddSealDirectory => Native as DirectGraphSealDirectory;
            public DirectGraphCompositeDirectory AddCompositeSharedOpaqueDirectory => Native as DirectGraphCompositeDirectory;
            public DirectGraphExport AddExport => Native as DirectGraphExport;
        }

        [Fact]
        public void RendererExpandsChunksAndSetsButPreservesArgumentsPathsAndLineEndings()
        {
            using var output = new StringWriter();
            Assert.Equal(3, Renderer.Render(new StringReader(RenderFragment), "graph.dg", output));
            string view = output.ToString();
            Assert.Contains("not DG input or an executable script", view);
            Assert.Contains("source: \"graph.dg:", view);
            Assert.Contains("arguments: concat \" \" escapeArguments", view);
            Assert.Contains("child[0]: stringRef \"-c\"", view);
            Assert.Contains("child[1]: concat \"\"", view);
            Assert.Contains("child[2]: stringRef \"\"", view);
            Assert.Contains("""stringRef "'literal'\r\n\nlast\rtail" """.TrimEnd(), view);
            Assert.Contains("""stringRef "\n" """.TrimEnd(), view);
            Assert.Contains("input: [\"out/a b\",\"out/a b\"]", view);
            Assert.Contains("content: [\"out/a b\",\"out/result\"]", view);
        }

        [Theory]
        [InlineData(null, "result", "process \"run\"")]
        [InlineData(null, "out/result", "process \"run\"")]
        [InlineData(null, "out\\result", "process \"run\"")]
        [InlineData(null, "a b", "writeFile \"script\"")]
        [InlineData("script", null, "writeFile \"script\"")]
        [InlineData("run", "result", "process \"run\"")]
        public void RendererFiltersByOperationAndOutput(string operation, string path, string expected)
        {
            using var output = new StringWriter();
            Assert.Equal(1, Renderer.Render(new StringReader(RenderFragment), "graph.dg", output, operation, path));
            Assert.Contains(expected, output.ToString());
        }

        [Theory]
        [InlineData("script")]
        [InlineData("absent")]
        public void RendererChecksTrailingSyntaxEvenWhenFiltered(string operation)
        {
            using var output = new StringWriter();
            Assert.Throws<InvalidDataException>(() =>
                Renderer.Render(new StringReader(RenderFragment + "\nstring 99 \"bad\""), "graph.dg", output, operation));
        }

        [Fact]
        public void RendererCommandReportsUsageNoMatchesAndParseErrors()
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, Renderer.Run(new[] { "--help" }, output, error));
            Assert.Equal(2, Renderer.Run(Array.Empty<string>(), output, error));
            Assert.Equal(2, Renderer.Run(new[] { "--unknown" }, output, error));
            Assert.Equal(2, Renderer.Run(new[] { "--operation" }, output, error));
            Assert.Equal(2, Renderer.Run(new[] { "--operation", "x", "--operation", "y" }, output, error));
            string file = Path.GetTempFileName();
            try
            {
                File.WriteAllText(file, RenderFragment);
                Assert.Equal(0, Renderer.Run(new[] { file, "--operation", "run", "--output", "result" }, output, error));
                Assert.Equal(1, Renderer.Run(new[] { file, "--operation", "run", "--output", "a b" }, output, error));
                Assert.Contains("No operations matched", error.ToString());
                File.WriteAllText(file, "directgraph 3\nwriteFile bad\n\tcontent s0\n");
                Assert.Equal(2, Renderer.Run(new[] { file }, output, error));
                Assert.Contains(file + ":3:", error.ToString());
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void ReaderStreamsAndPreservesRawPayloadsAndInternedValues()
        {
            using var reader = new StringReader(DirectGraphFixtureFiles.NormalizeSourceLiteral($$"""
                directgraph 3
                path 0 out/a b
                path 1 $literal
                string 0 $NOT_EXPANDED %HOME% \r\t\\\"\u0061
                set 0
                {{"\t"}}p0
                {{"\t"}}p1
                process first
                {{"\t"}}input @0
                {{"\t"}}environment s0 s0
                unknown invalid

                """));
            using var records = new DirectGraphTextReader(reader, "graph.dg").Read().GetEnumerator();
            Assert.True(records.MoveNext());
            Assert.Equal(new[] { "out/a b", "$literal" }, records.Current.Fields["input"][0]);
            Assert.Equal("""$NOT_EXPANDED %HOME% \r\t\\\"\u0061""",
                records.Current.PipDataFields["environment"][0].Expression.Value);
            Assert.Throws<InvalidDataException>(() => records.MoveNext());
        }

        [Theory]
        [InlineData("")]
        [InlineData("plain caf\u00e9")]
        [InlineData("prefix\nmida\tend")]
        [InlineData("\"quoted\"\\")]
        public void ReaderRetainsRawValuesAfterAdvancing(string expected)
        {
            string payload = expected.Replace("\n", "\n\t");
            using var reader = new StringReader($"directgraph 3\nstring 0 {payload}\nprocess first\n\tenvironment s0 s0\n\toption s0\nprocess second\n\tenvironment s0 s0\n");
            using var records = new DirectGraphTextReader(reader, "graph.dg").Read().GetEnumerator();
            Assert.True(records.MoveNext());
            var first = records.Current;
            Assert.True(records.MoveNext());
            Assert.Equal("first", first.Name);
            Assert.Equal(expected, first.PipDataFields["environment"][0].Expression.Value);
            Assert.Equal(expected, first.Fields["option"][0][0]);
            Assert.Equal(expected, records.Current.PipDataFields["environment"][0].Expression.Value);
            Assert.False(records.MoveNext());
        }

        [Theory]
        [InlineData("s12")]
        [InlineData("p12")]
        [InlineData("s00")]
        public void ReferenceSpellingsCannotNameRecords(string name)
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                new DirectGraphTextReader(new StringReader($"directgraph 3\nprocess {name}\n"), "names.dg").Read().ToArray());
            Assert.Contains("reserved", error.Message);
            Assert.Contains(name, error.Message);
            Assert.Contains("names.dg:", error.Message);
        }

        [Theory]
        [InlineData("option s0")]
        [InlineData("inputDirectory p0 s0")]
        public void ReferenceSpellingsCannotNameOptionsOrProducers(string field)
        {
            var error = Assert.Throws<InvalidDataException>(() => new DirectGraphTextReader(
                new StringReader($"directgraph 3\npath 0 /root\nstring 0 p12\nprocess run\n\t{field}\n"), "names.dg").Read().ToArray());
            Assert.Contains("reserved", error.Message);
            Assert.Contains("p12", error.Message);
        }

        [Fact]
        public void ReaderReportsFragmentWhenUtf8DecodingFails()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("directgraph 3\nstring 0 ").Concat(new byte[] { 0xff }).ToArray());
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var error = Assert.Throws<InvalidDataException>(() => new DirectGraphTextReader(reader, "invalid.dg").Read().ToArray());
            Assert.Contains("invalid.dg", error.Message);
            Assert.Contains("Invalid UTF-8", error.Message);
            Assert.IsType<DecoderFallbackException>(error.InnerException);
        }

        [Theory]
        [InlineData("directgraph 3\nstring 0 x\nstring 0 y\n")]
        [InlineData("directgraph 3\npath 1 x\n")]
        [InlineData("directgraph 3\nstring 01 x\n")]
        [InlineData("directgraph 3\nstring +0 x\n")]
        [InlineData("directgraph 3\nstring -0 x\n")]
        [InlineData("directgraph 3\nstring 2147483648 x\n")]
        [InlineData("directgraph 3\nset 1\n")]
        [InlineData("directgraph 3\nset 0\n\t@0\n")]
        [InlineData("directgraph 3\npath 0 x\nset 0\n\tp0\nprocess p\n\tinput @00\n")]
        [InlineData("directgraph 3\nwriteFile w\n\tcontent s0\n")]
        [InlineData("directgraph 3\nstring 0 x\nwriteFile w\n\tdestination s0\n")]
        [InlineData("directgraph 3\nwriteFile w\n\tcontent \"x\"\n")]
        [InlineData("directgraph 3\npath 0 x\nwriteFile w\n\tdestination p01\n")]
        [InlineData("directgraph 3\npath 0 x\nwriteFile w\n\tdestination p\n")]
        [InlineData("directgraph 3\n\tinput p0\n")]
        public void ReaderRejectsInvalidSyntax(string text) =>
            Assert.Throws<InvalidDataException>(() => new DirectGraphTextReader(new StringReader(text), "bad.dg").Read().ToArray());

        [Fact]
        public async Task FragmentLocalIdsResolveToNativeArtifactsAndStrings()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 out/a
                string 0 first
                writeFile first
                {{"\t"}}destination p0
                {{"\t"}}content s0

                """);
            AddPolicy(project, $$"""
                directgraph 3
                path 0 out/b
                path 1 out/a
                path 2 out/c
                string 0 second
                writeFile second
                {{"\t"}}destination p0
                {{"\t"}}content s0
                copyFile copy
                {{"\t"}}source p1
                {{"\t"}}destination p2

                """);
            await Load(new[] { project });
            var writes = m_operations.Where(operation => operation.AddWriteFile != null).Select(operation => operation.AddWriteFile).ToArray();
            Assert.Equal(new[] { Absolute("out/a"), Absolute("out/b") }, writes.Select(write => write.Destination));
            Assert.Equal(new[] { "first", "second" }, writes.Select(write => write.Content.ToString(m_paths)));
            var copy = m_operations.Single(operation => operation.AddCopyFile != null).AddCopyFile;
            Assert.Equal(Absolute("out/a"), copy.Source.Path);
            Assert.Equal(1, copy.Source.RewriteCount);
        }

        [Fact]
        public async Task ComposedArgumentsPreserveWhitespaceQuotesAndWholeArgumentEscaping()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 tool
                path 1 .
                path 2 out/result
                string 0 -c
                string 1   echo{{"\t"}}
                string 2 '$HOME "quoted"'\
                {{"\t"}}`backtick`''""
                string 3{{" "}}
                string 4{{"  "}}
                writeFile tool
                {{"\t"}}destination p0
                {{"\t"}}content s3
                process run
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}arguments concat s4 escapeArguments :
                {{"\t\t"}}s0
                {{"\t\t"}}concat - s1 s2 s1
                {{"\t\t"}}concat - s3 s3
                {{"\t"}}output p2

                """);
            await Load(new[] { project });
            var operation = Assert.Single(m_operations.Where(operation => operation.AddProcess != null));
            var arguments = operation.AddProcess.Arguments;
            var expected = PipDataBuilder.CreatePipData(m_paths.StringTable, " ", PipDataFragmentEscaping.CRuntimeArgumentRules,
                "-c", "  echo\t'$HOME \"quoted\"'\\\n`backtick`''\"\"  echo\t", "");
            Assert.Equal(expected.ToString(m_paths), arguments.ToString(m_paths));
            Assert.Equal(PipDataFragmentEscaping.EscapeEachArgument, arguments.FragmentEscaping);
            Assert.Equal(3, arguments.FragmentCount);
            var raw = arguments.Skip(1).First().GetNestedFragmentValue();
            Assert.Equal(PipDataFragmentEscaping.NoEscaping, raw.FragmentEscaping);
            Assert.Equal(3, raw.FragmentCount);
            Assert.Equal("  echo\t'$HOME \"quoted\"'\\\n`backtick`''\"\"  echo\t",
                string.Concat(raw.Select(fragment => fragment.GetStringIdValue().ToString(m_paths.StringTable))));
        }

        [Fact]
        public async Task ComposedContentPreservesRawBytesAndSharedChunks()
        {
            var project = Project("p", "directgraph 3\n" +
                "path 0 out/script.sh\n" +
                "string 0 echo\n" +
                "string 1  \t\n" +
                "string 2 '\"`$HOME\\'\r\n" +
                "\té\n" +
                "writeFile script\n" +
                "\tdestination p0\n" +
                "\tcontent concat - s0 s1 s2 s1 s0\n");
            await Load(new[] { project });
            var operation = Assert.Single(m_operations.Where(operation => operation.AddWriteFile != null));
            var content = operation.AddWriteFile.Content;
            Assert.Equal("echo \t'\"`$HOME\\'\r\n\u00e9 \techo", content.ToString(m_paths));
            var chunks = Assert.Single(content).GetNestedFragmentValue();
            Assert.Equal(chunks.First().GetStringIdValue(), chunks.Last().GetStringIdValue());
        }

        [Theory]
        [InlineData("")]
        [InlineData("\tcontent s0\n\tcontent concat - s0 s0\n")]
        public async Task WriteFileRequiresExactlyOneContentField(string content)
        {
            var project = Project("p", "directgraph 3\npath 0 out/file\nstring 0 x\nwriteFile file\n\tdestination p0\n" + content);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("Field 'content' must occur exactly once", error.Message);
        }

        [Fact]
        public async Task ConfiguredExportsBindInputsWithoutImportDeclarationsAndUnselectedFilesAreNotRead()
        {
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 out/a
                string 0 a
                writeFile write
                {{"\t"}}destination p0
                {{"\t"}}content s0

                """);
            producer.FileExports = new[] { Absolute("out/a") };
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 out/a
                path 1 out/b
                copyFile copy
                {{"\t"}}source p0
                {{"\t"}}destination p1

                """, "producer");
            var ignored = Project("ignored", "not a graph");
            await Load(new[] { consumer, ignored, producer }, new[] { "consumer" });
            Assert.Equal(2, m_opened.Count);
            var copy = Assert.Single(m_operations.Where(operation => operation.AddCopyFile != null)).AddCopyFile;
            Assert.Equal(
                m_outputs.Single(pair => pair.Key.Name == "producer").Value.Files[Absolute("out/a")],
                copy.Source);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrivateAndNondependencyOutputsCannotBecomeStaleSources(bool dependency)
        {
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 out/a
                string 0 a
                writeFile write
                {{"\t"}}destination p0
                {{"\t"}}content s0

                """);
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 out/a
                path 1 out/b
                copyFile copy
                {{"\t"}}source p0
                {{"\t"}}destination p1

                """, dependency ? new[] { "producer" } : Array.Empty<string>());
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { producer, consumer }));
            Assert.Contains("not exported", error.Message);
        }

        [Fact]
        public async Task ProjectLocalStreamsImportActualRewritesAndOrderDependencies()
        {
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 out/a
                path 1 tools/tool
                path 2 .
                string 0 data
                writeFile first
                {{"\t"}}destination p0
                {{"\t"}}content s0
                writeFile tool
                {{"\t"}}destination p1
                {{"\t"}}content s0
                process rewrite
                {{"\t"}}executable p1
                {{"\t"}}workingDirectory p2
                {{"\t"}}rewrite p0

                """);
            producer.FileExports = new[] { Absolute("out/a"), Absolute("tools/tool") };
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 .
                path 2 out/a
                path 3 out/result
                process consume
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}input p2
                {{"\t"}}orderOnly p2
                {{"\t"}}output p3

                """, "producer");
            await Load(new[] { consumer, producer });
            var processes = m_pipGraph.RetrieveScheduledPips().OfType<Process>().ToArray();
            var rewritten = Assert.Single(processes[0].FileOutputs).ToFileArtifact();
            Assert.Equal(2, rewritten.RewriteCount);
            Assert.Contains(rewritten, processes[1].Dependencies);
            Assert.Contains(processes[0].PipId, processes[1].OrderDependencies);
            Assert.Contains(rewritten, m_operations.Single(operation => operation.AddProcess?.Symbol == "consume").AddProcess.InputFiles);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TransitiveFilesRequireAnExplicitReexport(bool reexport)
        {
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 out/a
                string 0 data
                writeFile first
                {{"\t"}}destination p0
                {{"\t"}}content s0

                """);
            producer.FileExports = new[] { Absolute("out/a") };
            var forwarding = Project("forwarding", "directgraph 3\n", "producer");
            if (reexport)
            {
                forwarding.FileExports = producer.FileExports;
            }
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 out/a
                path 1 out/b
                copyFile copy
                {{"\t"}}source p0
                {{"\t"}}destination p1

                """, "forwarding");
            if (!reexport)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { consumer, forwarding, producer }));
                Assert.Contains("not exported", error.Message);
                return;
            }
            await Load(new[] { consumer, forwarding, producer });
            Assert.Equal(
                m_outputs.Single(pair => pair.Key.Name == "forwarding").Value.Files[Absolute("out/a")],
                Assert.Single(m_pipGraph.RetrieveScheduledPips().OfType<CopyFile>()).Source);
        }

        [Fact]
        public async Task DirectoryReexportsPreserveSealIdentityMembershipAndProducerNames()
        {
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 out/include/a.h
                path 1 out/include
                string 0 header
                writeFile write
                {{"\t"}}destination p0
                {{"\t"}}content s0
                sealDirectory headers
                {{"\t"}}directoryRoot p1
                {{"\t"}}content p0
                export public
                {{"\t"}}directory p1

                """);
            var forwarding = Project("forwarding", "directgraph 3\n", "producer");
            forwarding.DirectoryExports = new[] { Absolute("out/include") };
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 out/include
                path 1 out/include/a.h
                path 2 out/b.h
                sealDirectory selected
                {{"\t"}}directoryRoot p0
                {{"\t"}}sealKind full
                {{"\t"}}content p1
                {{"\t"}}outputDirectory p0 producer/headers
                copyFile copy
                {{"\t"}}source p1
                {{"\t"}}destination p2

                """, "forwarding");
            await Load(new[] { consumer, forwarding, producer });
            var original = m_outputs.Single(pair => pair.Key.Name == "producer").Value.Directories[Absolute("out/include")];
            var forwarded = m_outputs.Single(pair => pair.Key.Name == "forwarding").Value.Directories[Absolute("out/include")];
            Assert.Equal(original.Root, forwarded.Root);
            Assert.Equal(original.Contents.ToArray(), forwarded.Contents.ToArray());
            var seals = m_pipGraph.RetrieveScheduledPips().OfType<SealDirectory>().ToArray();
            Assert.Contains(original.Root, seals[1].OutputDirectoryContents);
            Assert.Equal(original.Contents[0], Assert.Single(m_pipGraph.RetrieveScheduledPips().OfType<CopyFile>()).Source);
        }

        [Fact]
        public async Task PolicySealsPublishStaticMembershipWithoutLosingTheOriginalProducer()
        {
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 out/include/a.h
                string 0 header
                writeFile write
                {{"\t"}}destination p0
                {{"\t"}}content s0

                """);
            AddPolicy(producer, $$"""
                directgraph 3
                path 0 out/include
                path 1 out/include/a.h
                sealDirectory headers
                {{"\t"}}directoryRoot p0
                {{"\t"}}content p1
                export public
                {{"\t"}}directory p0

                """);
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 out/include/a.h
                path 1 out/b.h
                copyFile copy
                {{"\t"}}source p0
                {{"\t"}}destination p1

                """, "producer");
            await Load(new[] { consumer, producer });
            var copy = Assert.Single(m_operations.Where(operation => operation.AddCopyFile != null)).AddCopyFile;
            Assert.Equal(
                m_outputs.Single(pair => pair.Key.Name == "producer").Value.Directories[Absolute("out/include")].Contents[0],
                copy.Source);
            Assert.Single(m_operations.Where(operation => operation.AddExport != null));
        }

        [Fact]
        public async Task ExplicitSourceFilesDoNotImplicitlyImportSourceDirectoryGrants()
        {
            var sources = Project("sources", $$"""
                directgraph 3
                path 0 src
                string 0 *.h
                sealDirectory source
                {{"\t"}}directoryRoot p0
                {{"\t"}}sealKind sourceTopDirectoryOnly
                {{"\t"}}pattern s0
                export public
                {{"\t"}}directory p0

                """);
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 src/tool.h
                path 1 .
                path 2 src/input.h
                path 3 out/compiled
                process compile
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}orderOnly p2
                {{"\t"}}output p3

                """, "sources");
            var probes = new List<AbsolutePath>();
            await Load(new[] { consumer, sources }, probe: path => { probes.Add(path); return true; });
            Assert.Equal(new[] { Absolute("src/input.h") }, probes);
            var process = m_operations.Single(operation => operation.AddProcess != null).AddProcess;
            Assert.Empty(process.InputFiles);
            Assert.Empty(process.OrderOnlyInputs);
            Assert.Empty(process.InputDirectories);
            Assert.Empty(Assert.Single(m_pipGraph.RetrieveScheduledPips().OfType<Process>()).DirectoryDependencies);
        }

        [Theory]
        [InlineData("src/sub/input.h")]
        [InlineData("src/input.c")]
        [InlineData("other/input.h")]
        [InlineData("src/.git/HEAD")]
        public async Task ExplicitSourcesDoNotRequireMatchingSourceAuthorization(string path)
        {
            var sources = Project("sources", $$"""
                directgraph 3
                path 0 src
                string 0 *.h
                string 1 .git
                sealDirectory source
                {{"\t"}}directoryRoot p0
                {{"\t"}}sealKind sourceTopDirectoryOnly
                {{"\t"}}pattern s0
                {{"\t"}}sourceExclusion s1
                export public
                {{"\t"}}directory p0

                """);
            var consumer = Project("consumer", "directgraph 3\npath 0 " + path + "\npath 1 out/b\ncopyFile c\n\tsource p0\n\tdestination p1\n", "sources");
            await Load(new[] { consumer, sources });
            var copy = Assert.Single(m_operations.Where(operation => operation.AddCopyFile != null)).AddCopyFile;
            Assert.True(copy.Source.IsSourceFile);
        }

        [Theory]
        [InlineData(false, false, 1)]
        [InlineData(false, true, 1)]
        [InlineData(true, false, 1)]
        [InlineData(true, true, 1)]
        [InlineData(false, false, 2)]
        [InlineData(false, true, 2)]
        [InlineData(true, false, 2)]
        [InlineData(true, true, 2)]
        public async Task ExplicitSourcesCannotMaskSiblingOutputsInEitherLoadOrder(bool sourceFirst, bool directory, int maxConcurrency)
        {
            using var claimed = new ManualResetEventSlim();
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 out/input
                path 1 result/copy
                copyFile read
                {{"\t"}}source p0
                {{"\t"}}destination p1

                """);
            var producer = Project("producer", directory ? $$"""
                directgraph 3
                path 0 tool
                path 1 .
                path 2 out
                process produce
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}outputDirectory p2 shared

                """ : $$"""
                directgraph 3
                path 0 out/input
                string 0 produced
                writeFile produce
                {{"\t"}}destination p0
                {{"\t"}}content s0

                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Load(sourceFirst ? new[] { consumer, producer } : new[] { producer, consumer },
                    maxConcurrency: maxConcurrency,
                    beforeOpen: file =>
                    {
                        if (maxConcurrency > 1 && file == Absolute(sourceFirst ? "producer.dg" : "consumer.dg"))
                        {
                            Assert.True(claimed.Wait(TimeSpan.FromSeconds(30)), "The first project never claimed its paths.");
                        }
                    },
                    beforeSend: operation =>
                    {
                        if (sourceFirst ? operation.AddCopyFile?.Module == "consumer" :
                            operation.AddWriteFile?.Module == "producer" || operation.AddProcess?.Module == "producer")
                        {
                            claimed.Set();
                        }
                    }));
            Assert.Equal(Absolute(sourceFirst ? "consumer.dg" : "producer.dg"), m_opened[0]);
            Assert.Contains("Generated input", error.Message);
            Assert.Contains("not exported by a declared dependency", error.Message);
            Assert.Contains("consumer/read", error.Message);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SourceSealOutputOverlapIsDeferredToNativeGraphValidation(bool sourceFirst)
        {
            var source = Project("source", "directgraph 3\npath 0 out\nsealDirectory s\n\tdirectoryRoot p0\n\tsealKind sourceAllDirectories\n");
            var generated = Project("generated", "directgraph 3\npath 0 out/a\nstring 0 a\nwriteFile w\n\tdestination p0\n\tcontent s0\n");
            await Load(sourceFirst ? new[] { source, generated } : new[] { generated, source });
            Assert.Contains(m_operations, operation => operation.AddWriteFile != null);
            Assert.Contains(m_operations, operation => operation.AddSealDirectory != null);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task SourceSealsInsideSharedOpaquesArePassedToNativeConstruction(bool sourceFirst)
        {
            var source = Project("source", "directgraph 3\npath 0 out/src\nsealDirectory s\n\tdirectoryRoot p0\n\tsealKind sourceAllDirectories\n");
            var generated = Project("generated", $$"""
                directgraph 3
                path 0 tool
                path 1 .
                path 2 out
                process produce
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}outputDirectory p2 shared

                """);
            await Load(sourceFirst ? new[] { source, generated } : new[] { generated, source });
            Assert.Contains(m_operations, operation => operation.AddProcess != null);
            Assert.Contains(m_operations, operation => operation.AddSealDirectory != null);
        }

        [Fact]
        public async Task GeneratedExecutablesAndSchedulingOnlySetsKeepTheirProducer()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 out/tool
                path 1 .
                path 2 out/result
                string 0 script
                set 0
                {{"\t"}}p0
                writeFile tool
                {{"\t"}}destination p0
                {{"\t"}}content s0
                process use
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}orderOnly @0
                {{"\t"}}output p2

                """);
            await Load(new[] { project });
            var process = m_operations.Single(operation => operation.AddProcess != null).AddProcess;
            Assert.Equal(Absolute("out/tool"), process.Executable.Path);
            Assert.Equal(1, process.Executable.RewriteCount);
            Assert.Equal(process.Executable, Assert.Single(process.OrderOnlyInputs).File);
            Assert.True(Assert.Single(process.OrderOnlyInputs).Producer.IsValid);
        }

        [Theory]
        [InlineData("writeFile w\n\tdestination p0\n\tcontent s0\n\ttypo x\n")]
        [InlineData("writeFile w\n\tdestination p0\n\tdestination p1\n\tcontent s0\n")]
        [InlineData("copyFile c\n\tsource p1\n\tdestination p0\n\ttypo x\n")]
        [InlineData("writeFile w\n\tdestination p0\n\tcontent s0\nwriteFile w\n\tdestination p1\n\tcontent s0\n")]
        public async Task SemanticErrorsHaveFragmentAndOperationContext(string body)
        {
            var project = Project("p", "directgraph 3\npath 0 out/a\npath 1 out/b\nstring 0 a\n" + body);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("p.dg:", error.Message);
            Assert.Contains("(p/", error.Message);
        }

        [Fact]
        public async Task EntireProjectDagIsValidatedBeforeOpeningAnyFragment()
        {
            var root = Project("root", "directgraph 3\n");
            var a = Project("a", "directgraph 3\n", "b");
            var b = Project("b", "directgraph 3\n", "a");
            await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { root, a, b }, new[] { "root" }));
            Assert.Empty(m_opened);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OverlappingOpaqueRootsRequireAnExplicitDirectoryIdentity(bool explicitIdentity)
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 .
                path 2 out/shared
                path 3 out/shared/member
                path 4 out/result
                string 0 executable
                writeFile tool
                {{"\t"}}destination p0
                {{"\t"}}content s0
                process left
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}outputDirectory p2
                process right
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}outputDirectory p2
                process consume
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}input p3
                {{"\t"}}output p4

                """ + (explicitIdentity ? "\tinputDirectory p2 left\n" : ""));
            if (!explicitIdentity)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
                Assert.Contains("unambiguous directory", error.Message);
                return;
            }
            await Load(new[] { project });
            var consumer = m_operations.Single(operation => operation.AddProcess?.Symbol == "consume").AddProcess;
            Assert.Equal(m_pipGraph.RetrieveScheduledPips().OfType<Process>().First().DirectoryOutputs[0],
                Assert.Single(consumer.InputDirectories));
            Assert.Empty(consumer.InputFiles);
        }

        [Fact]
        public async Task KnownOpaqueMembersKeepTheirProducedFileArtifacts()
        {
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 .
                path 2 out/opaque
                path 3 out/opaque/member
                string 0 tool
                writeFile tool
                {{"\t"}}destination p0
                {{"\t"}}content s0
                process generate
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}output p3
                {{"\t"}}outputDirectory p2

                """);
            producer.FileExports = new[] { Absolute("tools/tool") };
            producer.DirectoryExports = new[] { Absolute("out/opaque") };
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 .
                path 2 out/opaque/member
                path 3 out/result
                process consume
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}input p2
                {{"\t"}}output p3

                """, "producer");
            await Load(new[] { consumer, producer });
            var request = m_operations.Single(operation => operation.AddProcess?.Symbol == "consume").AddProcess;
            var reference = Assert.Single(request.InputFiles);
            Assert.True(reference.IsOutputFile);
            var processes = m_pipGraph.RetrieveScheduledPips().OfType<Process>().ToArray();
            Assert.Contains(Assert.Single(processes[0].FileOutputs).ToFileArtifact(), processes[1].Dependencies);
            Assert.Single(request.InputDirectories);
        }

        [Fact]
        public async Task UnlistedOpaqueExecutablesRequireConcreteArtifacts()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 .
                path 2 out/opaque
                path 3 out/opaque/executable
                string 0 tool
                writeFile tool
                {{"\t"}}destination p0
                {{"\t"}}content s0
                process generate
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}outputDirectory p2
                process consume
                {{"\t"}}executable p3
                {{"\t"}}workingDirectory p1

                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("concrete file artifact", error.Message);
        }

        [Fact]
        public async Task OpaqueExportsDoNotExposeUnrelatedPrivateExplicitOutputs()
        {
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 out/opaque/private
                path 2 .
                path 3 out/opaque
                string 0 executable
                string 1 private
                writeFile tool
                {{"\t"}}destination p0
                {{"\t"}}content s0
                writeFile private
                {{"\t"}}destination p1
                {{"\t"}}content s1
                process opaque
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p2
                {{"\t"}}outputDirectory p3

                """);
            producer.FileExports = new[] { Absolute("tools/tool") };
            producer.DirectoryExports = new[] { Absolute("out/opaque") };
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 .
                path 2 out/opaque/private
                path 3 out/result
                process consume
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}input p2
                {{"\t"}}output p3

                """, "producer");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { consumer, producer }));
            Assert.Contains("private explicit file", error.Message);
        }

        [Fact]
        public async Task BuilderRejectionsStopConstructionBeforeCompletion()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 out/a
                path 1 out/b
                string 0 a
                string 1 b
                writeFile rejected
                {{"\t"}}destination p0
                {{"\t"}}content s0
                writeFile neverRead
                {{"\t"}}destination p1
                {{"\t"}}content s1

                """);
            int sent = 0;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }, reject: operation =>
            {
                sent++;
                throw new DirectGraphConstructionException("pip validation rejected");
            }));
            Assert.Contains("pip validation rejected", error.Message);
            Assert.Equal(1, sent);
        }

        [Fact]
        public async Task OpaqueExportsDoNotExposeOtherProjectsPrivateFilesWithMatchingLocalIds()
        {
            var unrelated = Project("unrelated", $$"""
                directgraph 3
                path 0 out/other
                path 1 out/opaque/private
                string 0 private
                writeFile first
                {{"\t"}}destination p0
                {{"\t"}}content s0
                writeFile private
                {{"\t"}}destination p1
                {{"\t"}}content s0

                """);
            var producer = Project("producer", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 .
                path 2 out/opaque
                string 0 tool
                writeFile tool
                {{"\t"}}destination p0
                {{"\t"}}content s0
                process opaque
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}outputDirectory p2

                """, "unrelated");
            producer.FileExports = new[] { Absolute("tools/tool") };
            producer.DirectoryExports = new[] { Absolute("out/opaque") };
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 tools/tool
                path 1 .
                path 2 out/opaque/private
                path 3 out/result
                process consume
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}input p2
                {{"\t"}}output p3

                """, "producer");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { consumer, producer, unrelated }));
            Assert.Contains("private explicit file", error.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpaqueMemberChecksDoNotDependOnSiblingFileClaimOrder(bool readerFirst)
        {
            const string header = "directgraph 3\npath 0 tool\npath 1 .\npath 2 out/opaque\npath 3 out/opaque/private\npath 4 out/result\nstring 0 content\n";
            var provider = Project("provider", header +
                "process opaque\n\texecutable p0\n\tworkingDirectory p1\n\toutputDirectory p2 shared\nexport publish\n\tdirectory p2\n");
            var owner = Project("owner", header + "writeFile private\n\tdestination p3\n\tcontent s0\n");
            var consumer = Project("consumer", header +
                "process read\n\texecutable p0\n\tworkingDirectory p1\n\tinput p3\n\toutput p4\n", "provider");
            using var claimed = new ManualResetEventSlim();
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Load(new[] { consumer, provider, owner }, maxConcurrency: 2,
                    beforeOpen: file =>
                    {
                        if (file == Absolute(readerFirst ? "owner.dg" : "consumer.dg"))
                        {
                            Assert.True(claimed.Wait(TimeSpan.FromSeconds(30)));
                        }
                    },
                    beforeSend: operation =>
                    {
                        if (readerFirst ? operation.AddProcess?.Module == "consumer" :
                            operation.AddWriteFile?.Module == "owner")
                        {
                            claimed.Set();
                        }
                    }));
            Assert.Contains("Opaque export does not expose private explicit file", error.Message);
        }

        [Fact]
        public async Task ConfiguredOutputsOfUnselectedProjectsCannotBeHiddenBySourceSeals()
        {
            var ignored = Project("ignored", "not read");
            ignored.FileExports = new[] { Absolute("src/generated.h") };
            var selected = Project("selected", $$"""
                directgraph 3
                path 0 src
                path 1 src/generated.h
                path 2 result/copied
                sealDirectory source
                {{"\t"}}directoryRoot p0
                {{"\t"}}sealKind sourceAllDirectories
                copyFile read
                {{"\t"}}source p1
                {{"\t"}}destination p2

                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { ignored, selected }, new[] { "selected" }));
            Assert.Contains("not exported by a declared dependency", error.Message);
            Assert.Equal(new[] { Absolute("selected.dg") }, m_opened);
        }

        [Theory]
        [InlineData("out/a")]
        [InlineData("out/./a")]
        public async Task DuplicateSealMembersReportRecordContext(string second)
        {
            var project = Project("duplicate", $$"""
                directgraph 3
                path 0 out/a
                path 1 out
                path 2 {{second}}
                string 0 content
                writeFile first
                {{"\t"}}destination p0
                {{"\t"}}content s0
                sealDirectory headers
                {{"\t"}}directoryRoot p1
                {{"\t"}}content p0
                {{"\t"}}content p2

                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("duplicate.dg:9", error.Message);
            Assert.Contains("duplicate/headers", error.Message);
            Assert.Contains("Duplicate seal content", error.Message);
        }

        [Fact]
        public async Task TempAndSemaphoreFieldsUseNativeConstruction()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 tool
                path 1 .
                path 2 out/one
                path 3 out/two
                path 4 scratch
                path 5 out/three
                path 6 out/four
                string 0 mutex
                writeFile tool
                {{"\t"}}destination p0
                {{"\t"}}content s0
                process one
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}output p2
                {{"\t"}}enableTempDirectory true
                {{"\t"}}semaphore s0 1 1
                process two
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}output p3
                {{"\t"}}tempDirectory p4
                {{"\t"}}semaphore s0 1 1
                process three
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}output p5
                {{"\t"}}enableTempDirectory true
                {{"\t"}}semaphore s0 1 1
                process four
                {{"\t"}}executable p0
                {{"\t"}}workingDirectory p1
                {{"\t"}}output p6

                """);
            await Load(new[] { project });
            var processes = m_pipGraph.RetrieveScheduledPips().OfType<Process>().ToArray();
            Assert.Equal(4, processes.Length);
            Assert.True(processes[0].TempDirectory.IsValid);
            Assert.Equal(Absolute("scratch"), processes[1].TempDirectory);
            Assert.NotEqual(processes[0].TempDirectory, processes[1].TempDirectory);
            Assert.True(processes[2].TempDirectory.IsValid);
            Assert.NotEqual(processes[0].TempDirectory, processes[2].TempDirectory);
            Assert.False(processes[3].TempDirectory.IsValid);
            Assert.All(processes.Take(3), process =>
            {
                Assert.Contains(process.TempDirectory, process.UntrackedScopes);
                Assert.Equal(1, Assert.Single(process.Semaphores).Limit);
                Assert.Contains(process.EnvironmentVariables, entry => entry.Name.ToString(m_paths.StringTable) == "TMPDIR");
            });
        }

        [Theory]
        [InlineData("enableTempDirectory true\n\ttempDirectory p1\n")]
        [InlineData("enableTempDirectory maybe\n")]
        [InlineData("semaphore s0 0 1\n")]
        [InlineData("semaphore s0 1 2\n")]
        [InlineData("semaphore s0 1 1\n\tsemaphore s0 1 1\n")]
        public async Task InvalidTempAndSemaphoreFieldsFailWithRecordContext(string fields)
        {
            var project = Project("p", "directgraph 3\npath 0 tool\npath 1 .\npath 2 out\nstring 0 mutex\nwriteFile tool\n\tdestination p0\n\tcontent s0\nprocess run\n\texecutable p0\n\tworkingDirectory p1\n\toutput p2\n\t" + fields);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("p/run", error.Message);
        }

        [Theory]
        [InlineData(false, "scratch/child/..", true)]
        [InlineData(true, "scratch/child/..", true)]
        [InlineData(true, "scratch/child", true)]
        [InlineData(true, "scratch-other", false)]
        public async Task ExplicitTempClaimsAreIsolatedAcrossProcessesAndProjects(bool acrossProjects, string secondTemp, bool rejected)
        {
            string header = $"directgraph 3\npath 0 tool\npath 1 .\npath 2 scratch\npath 3 {secondTemp}\npath 4 out/one\npath 5 out/two\n";
            string process(string name, int temp, int output) =>
                $"process {name}\n\texecutable p0\n\tworkingDirectory p1\n\ttempDirectory p{temp}\n\toutput p{output}\n";
            var first = Project("first", header + process("one", 2, 4) + (acrossProjects ? "" : process("two", 3, 5)));
            var projects = acrossProjects
                ? new[] { Project("second", header + process("two", 3, 5), "first"), first }
                : new[] { first };
            if (rejected)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(projects));
                Assert.Contains(acrossProjects ? "(second/two)" : "(first/two)", error.Message);
                Assert.Contains(".dg:", error.Message);
                Assert.Contains("overlaps explicit temp directory", error.Message);
                Assert.Contains("claimed by 'first/one'", error.Message);
            }
            else
            {
                await Load(projects);
                var processes = m_pipGraph.RetrieveScheduledPips().OfType<Process>().ToArray();
                Assert.Equal(2, processes.Length);
                Assert.NotEqual(processes[0].TempDirectory, processes[1].TempDirectory);
            }
        }

        [Theory]
        [InlineData("region/file", "region", false, false)]
        [InlineData("region/file", "region", false, true)]
        [InlineData("region", "region/scratch", true, false)]
        [InlineData("region", "region/scratch", true, true)]
        [InlineData("region", "region", false, false)]
        [InlineData("region", "region", false, true)]
        public async Task ExplicitTempsCannotOverlapOutputsInEitherDeclarationOrder(string output, string temp, bool directory, bool tempFirst)
        {
            string header = $"directgraph 3\npath 0 tool\npath 1 .\npath 2 {output}\npath 3 {temp}\npath 4 elsewhere/result\nstring 0 content\n";
            const string scratch = "process scratch\n\texecutable p0\n\tworkingDirectory p1\n\ttempDirectory p3\n\toutput p4\n";
            string generated = directory
                ? "process generated\n\texecutable p0\n\tworkingDirectory p1\n\toutputDirectory p2 shared\n"
                : "writeFile generated\n\tdestination p2\n\tcontent s0\n";
            var project = Project("p", header + (tempFirst ? scratch + generated : generated + scratch));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains(tempFirst ? "(p/generated)" : "(p/scratch)", error.Message);
            Assert.Contains("p.dg:", error.Message);
            Assert.Contains("overlaps", error.Message);
            Assert.Contains("temp directory", error.Message);
        }

        [Fact]
        public async Task SourceExclusionsReachNativeSealWithoutGatingExplicitFiles()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 src
                path 1 src/.git/HEAD
                path 2 .
                path 3 out/result
                string 0 .git
                string 1 nested/private
                sealDirectory source
                {{"\t"}}directoryRoot p0
                {{"\t"}}sealKind sourceAllDirectories
                {{"\t"}}sourceExclusion s0
                {{"\t"}}sourceExclusion s1
                process read
                {{"\t"}}executable p1
                {{"\t"}}workingDirectory p2
                {{"\t"}}input p1
                {{"\t"}}output p3

                """);
            await Load(new[] { project });
            var seal = Assert.Single(m_pipGraph.RetrieveScheduledPips().OfType<SealDirectory>());
            Assert.Equal(new[] { ".git", "nested/private" },
                seal.SourceExclusions.Paths.Select(path => path.ToString(m_paths.StringTable)));
            Assert.True(seal.SourceExclusions.Contains(m_paths, seal.DirectoryRoot, Absolute("src/.git/HEAD")));
            Assert.False(seal.SourceExclusions.Contains(m_paths, seal.DirectoryRoot, Absolute("src/nested/.git/HEAD")));
            var process = Assert.Single(m_pipGraph.RetrieveScheduledPips().OfType<Process>());
            Assert.Empty(process.DirectoryDependencies);
            Assert.Contains(FileArtifact.CreateSourceFile(Absolute("src/.git/HEAD")), process.Dependencies);
        }

        [Theory]
        [InlineData("partial", ".git")]
        [InlineData("sourceAllDirectories", "")]
        [InlineData("sourceAllDirectories", "../outside")]
        public async Task InvalidSourceExclusionsAreRejected(string kind, string exclusion)
        {
            var project = Project("p", $"directgraph 3\npath 0 src\nstring 0 {exclusion}\nsealDirectory s\n\tdirectoryRoot p0\n\tsealKind {kind}\n\tsourceExclusion s0\n");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("p/s", error.Message);
            Assert.Contains("exclusion", error.Message);
        }

        [Fact]
        public async Task ExplicitSourcesWorkWithoutAnySourceSeal()
        {
            var project = Project("p", "directgraph 3\npath 0 source\npath 1 out\ncopyFile copy\n\tsource p0\n\tdestination p1\n");
            await Load(new[] { project });
            Assert.True(Assert.Single(m_pipGraph.RetrieveScheduledPips().OfType<CopyFile>()).Source.IsSourceFile);
        }

        [Fact]
        public async Task ConfigurationAndFragmentExportsMergeIdenticalArtifactsBeforeSending()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 out/file
                path 1 out
                string 0 content
                writeFile file
                {{"\t"}}destination p0
                {{"\t"}}content s0
                sealDirectory seal
                {{"\t"}}directoryRoot p1
                {{"\t"}}content p0
                export first
                {{"\t"}}file p0
                {{"\t"}}file p0
                {{"\t"}}directory p1
                export again
                {{"\t"}}file p0
                {{"\t"}}directory p1 seal

                """);
            project.FileExports = new[] { Absolute("out/file"), Absolute("out/file") };
            project.DirectoryExports = new[] { Absolute("out"), Absolute("out") };
            await Load(new[] { project });
            var export = Assert.Single(m_operations.Where(operation => operation.AddExport != null)).AddExport;
            Assert.Single(export.Files);
            Assert.Single(export.Directories);
            var outputs = Assert.Single(m_outputs).Value;
            Assert.Single(outputs.Files);
            Assert.Single(outputs.Directories);
        }

        [Fact]
        public async Task SamePathDifferentDirectoryArtifactsCannotBeMerged()
        {
            var project = Project("p", $$"""
                directgraph 3
                path 0 out
                path 1 tool
                path 2 .
                process first
                {{"\t"}}executable p1
                {{"\t"}}workingDirectory p2
                {{"\t"}}outputDirectory p0 shared
                process second
                {{"\t"}}executable p1
                {{"\t"}}workingDirectory p2
                {{"\t"}}outputDirectory p0 shared
                export one
                {{"\t"}}directory p0 first
                export two
                {{"\t"}}directory p0 second

                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("Conflicting directory export identities", error.Message);
        }

        [Fact]
        public async Task IndependentProjectsConstructConcurrentlyWithinTheBound()
        {
            var projects = Enumerable.Range(0, 3).Select(index => Project("p" + index,
                $"directgraph 3\npath 0 out/{index}\nstring 0 value{index}\nwriteFile write\n\tdestination p0\n\tcontent s0\n")).ToArray();
            using var entered = new CountdownEvent(2);
            using var release = new ManualResetEventSlim();
            int calls = 0, active = 0, peak = 0;
            var statsLock = new object();
            var run = Load(projects, maxConcurrency: 2, beforeSend: operation =>
            {
                if (operation.AddWriteFile == null)
                {
                    return;
                }
                int current = Interlocked.Increment(ref active);
                lock (statsLock)
                {
                    peak = Math.Max(peak, current);
                }
                if (Interlocked.Increment(ref calls) <= 2)
                {
                    entered.Signal();
                }
                try
                {
                    Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "Parallel construction was not released.");
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "Independent native calls were serialized.");
                Assert.Equal(2, Volatile.Read(ref active));
                Assert.Equal(2, Volatile.Read(ref calls));
            }
            finally
            {
                release.Set();
                await run;
            }
            Assert.Equal(2, peak);
            Assert.Equal(3, calls);
            Assert.Equal(3, m_pipGraph.RetrieveScheduledPips().OfType<WriteFile>().Count());
        }

        [Fact]
        public async Task DependentsWaitForPublishedExportsButNotUnrelatedProjects()
        {
            var provider = Project("provider", $$"""
                directgraph 3
                path 0 out/include/file
                path 1 out/include
                string 0 content
                writeFile write
                {{"\t"}}destination p0
                {{"\t"}}content s0
                sealDirectory seal
                {{"\t"}}directoryRoot p1
                {{"\t"}}content p0
                export publish
                {{"\t"}}file p0
                {{"\t"}}directory p1

                """);
            var consumer = Project("consumer", $$"""
                directgraph 3
                path 0 out/include/file
                path 1 out/include
                path 2 tool
                path 3 .
                path 4 out/result
                process use
                {{"\t"}}executable p2
                {{"\t"}}workingDirectory p3
                {{"\t"}}input p0
                {{"\t"}}inputDirectory p1 provider/seal
                {{"\t"}}output p4

                """, "provider");
            var other = Project("other", "directgraph 3\npath 0 out/other\nstring 0 other\nwriteFile write\n\tdestination p0\n\tcontent s0\n");
            using var providerBlocked = new ManualResetEventSlim();
            using var otherBlocked = new ManualResetEventSlim();
            using var consumerStarted = new ManualResetEventSlim();
            using var releaseProvider = new ManualResetEventSlim();
            using var releaseOther = new ManualResetEventSlim();
            var run = Load(new[] { consumer, other, provider }, maxConcurrency: 2,
                beforeOpen: file =>
                {
                    if (file == Absolute("consumer.dg"))
                    {
                        lock (m_operations)
                        {
                            Assert.Contains(m_operations, operation => operation.AddExport?.Module == "provider");
                        }
                        consumerStarted.Set();
                    }
                },
                beforeSend: operation =>
                {
                    if (operation.AddExport?.Module == "provider")
                    {
                        providerBlocked.Set();
                        Assert.True(releaseProvider.Wait(TimeSpan.FromSeconds(30)));
                    }
                    if (operation.AddWriteFile?.Module == "other")
                    {
                        otherBlocked.Set();
                        Assert.True(releaseOther.Wait(TimeSpan.FromSeconds(30)));
                    }
                });
            try
            {
                Assert.True(providerBlocked.Wait(TimeSpan.FromSeconds(30)));
                Assert.True(otherBlocked.Wait(TimeSpan.FromSeconds(30)));
                Assert.False(consumerStarted.IsSet);
                releaseProvider.Set();
                Assert.True(consumerStarted.Wait(TimeSpan.FromSeconds(30)), "An unrelated project held back a ready dependent.");
            }
            finally
            {
                releaseProvider.Set();
                releaseOther.Set();
                await run;
            }
            Assert.Single(m_operations.Where(operation => operation.AddProcess?.Module == "consumer"));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ConfiguredExportErrorsHaveTheirOwnPhaseContext(bool directory)
        {
            var project = Project("p", "directgraph 3\npath 0 out/file\nstring 0 content\nwriteFile last\n\tdestination p0\n\tcontent s0\n");
            if (directory)
            {
                project.DirectoryExports = new[] { Absolute("missing/directory") };
            }
            else
            {
                project.FileExports = new[] { Absolute("missing/file") };
            }
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("Project 'p' configured exports:", error.Message);
            Assert.DoesNotContain("(p/last)", error.Message);
        }

        [Fact]
        public async Task FailedProjectDoesNotStartDependents()
        {
            var failed = Project("failed", "directgraph 3\npath 0 out/bad\nstring 0 bad\nwriteFile bad\n\tdestination p0\n\tcontent s0\n\tunsupported true\n");
            var dependent = Project("dependent", "directgraph 3\n", "failed");
            var unrelated = Project("unrelated", "directgraph 3\n");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Load(new[] { dependent, unrelated, failed }, maxConcurrency: 3));
            Assert.Contains("failed/bad", error.Message);
            Assert.DoesNotContain(Absolute("dependent.dg"), m_opened);
        }

        [Theory]
        [InlineData("orderOnly")]
        [InlineData("input")]
        [InlineData("executable")]
        public async Task PendingOutputsCannotBeUsedByTheirOwnOperation(string field)
        {
            string executable = field == "executable" ? "p2" : "p0";
            string input = field == "executable" ? "" : $"\t{field} p2\n";
            var project = Project("p", $"directgraph 3\npath 0 tool\npath 1 .\npath 2 out/result\nprocess self\n\texecutable {executable}\n\tworkingDirectory p1\n{input}\toutput p2\n");
            int probes = 0;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Load(new[] { project }, probe: _ => { probes++; return true; }));
            Assert.Contains("p/self", error.Message);
            Assert.Contains("refers to an output of the same operation", error.Message);
            Assert.Equal(0, probes);
            Assert.Empty(m_operations);
        }

        [Fact]
        public async Task MultipleNativeOutputsBindByCachedPathRatherThanResultPosition()
        {
            for (int i = 63; i >= 0; i--) Absolute("out/file" + i);
            for (int i = 11; i >= 0; i--) Absolute("out/dir" + i);
            string registrations = string.Join("\n", Enumerable.Range(0, 64).Select(i => $"path {i + 2} out/file{i}")
                .Concat(Enumerable.Range(0, 12).Select(i => $"path {i + 66} out/dir{i}")));
            string fields = string.Join("\n", Enumerable.Range(0, 64).Select(i => $"\toutput p{i + 2}")
                .Concat(Enumerable.Range(0, 12).Select(i => $"\toutputDirectory p{i + 66} shared")));
            var project = Project("p", $"directgraph 3\npath 0 tool\npath 1 .\n{registrations}\nprocess many\n\texecutable p0\n\tworkingDirectory p1\n{fields}\n");
            project.FileExports = Enumerable.Range(0, 64).Select(i => Absolute("out/file" + i)).ToArray();
            project.DirectoryExports = Enumerable.Range(0, 12).Select(i => Absolute("out/dir" + i)).ToArray();
            await Load(new[] { project });
            var outputs = Assert.Single(m_outputs).Value;
            Assert.All(outputs.Files, pair => Assert.Equal(pair.Key, pair.Value.Path));
            Assert.All(outputs.Directories, pair => Assert.Equal(pair.Key, pair.Value.Root.Path));
            Assert.Equal(64, outputs.Files.Count);
            Assert.Equal(12, outputs.Directories.Count);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task MissingNativeOutputsReportTheDeclaringRecord(bool directory)
        {
            var project = Project("p", directory
                ? "directgraph 3\npath 0 tool\npath 1 .\npath 2 out/dir\nprocess missing\n\texecutable p0\n\tworkingDirectory p1\n\toutputDirectory p2 shared\n"
                : "directgraph 3\npath 0 out/file\nstring 0 value\nwriteFile missing\n\tdestination p0\n\tcontent s0\n");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project },
                nativeResult: result => directory ? result with { Directories = Array.Empty<DirectoryArtifact>() } :
                    result with { Files = Array.Empty<FileArtifact>() }));
            Assert.Contains("p.dg:", error.Message);
            Assert.Contains("p/missing", error.Message);
            Assert.Contains(directory ? "declared directory" : "declared file", error.Message);
        }

        [Fact]
        public async Task CkatiV3GoldenCorpusPreservesPayloadsAndNativeArtifacts()
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "KatiV3");
            string text = await File.ReadAllTextAsync(Path.Combine(directory, "golden.dg"), new UTF8Encoding(false, true));
            var expected = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(
                await File.ReadAllTextAsync(Path.Combine(directory, "expected.json"), new UTF8Encoding(false, true)));
            var records = new DirectGraphTextReader(new StringReader(text), "ckati-golden.dg").Read().ToArray();
            var processes = records.Where(record => record.Kind == "process").ToArray();
            Assert.Equal(3, processes.Length);
            static string Raw(DirectGraphTextExpression expression) => expression.Kind == "concat"
                ? string.Join(expression.Value, expression.Children.Select(Raw)) : expression.Value;
            foreach (var process in processes)
            {
                var environment = process.PipDataFields["environment"].ToDictionary(field => field.Parameters[0], field => Raw(field.Expression));
                foreach (var pair in expected) Assert.Equal(pair.Value, environment[pair.Key]);
                var arguments = Assert.Single(process.PipDataFields["arguments"]).Expression;
                Assert.True(arguments.EscapeArguments);
                Assert.Equal(new[] { "--noprofile", "", " ", "-c" }, arguments.Children.Take(4).Select(Raw));
            }
            Assert.Contains(processes, process => Raw(Assert.Single(process.PipDataFields["arguments"]).Expression).Contains("$(__kati_shell_result=$("));
            Assert.True(text.Split('\n').Max(line => line.Length) > 8192);

            await Load(new[] { Project("golden", text) });
            Assert.Equal(3, m_operations.Count(operation => operation.AddProcess != null));
            foreach (var operation in m_operations.Where(operation => operation.AddProcess != null))
            {
                var environment = operation.AddProcess.EnvironmentVariables.ToDictionary(
                    variable => variable.Name.ToString(m_paths.StringTable), variable => variable.Data.IsValid
                        ? variable.Data.ToString(m_paths) : variable.Value.ToString(m_paths.StringTable));
                foreach (var pair in expected) Assert.Equal(pair.Value, environment[pair.Key]);
            }
            var seal = Assert.Single(m_operations.Where(operation => operation.AddSealDirectory != null)).AddSealDirectory;
            Assert.Equal(global::BuildXL.Pips.Operations.SealDirectoryKind.Partial, seal.Kind);
            Assert.Equal(2, seal.Contents.Count);
            var manifest = Assert.Single(m_operations.Where(operation => operation.AddWriteFile != null)).AddWriteFile;
            Assert.Equal("[\"alpha\",\"beta\"]\n", manifest.Content.ToString(m_paths));
        }

        [Fact]
        public async Task TabFramedPipDataPreservesRawLiteralsAndDeferredPaths()
        {
            string payload = "first\r\n\tsecond\n";
            var project = Project("p",
                "directgraph 3\npath 0 tool\npath 1 .\npath 2 include dir\npath 3 out/result\n" +
                "string 0 first\r\n\t\tsecond\n\t\nstring 1 NAME\nstring 2 -I\nstring 3  \n" +
                "string 4 \tstart\r\n\t\tend\n\t\nstring 5 \nstring 6 prefix:\n" +
                "string 7 SCALAR\nstring 8 $HOME \"quoted\"\nstring 9 lane\n" +
                "writeFile tool\n\tdestination p0\n\tcontent concat - s0 s4\n" +
                "process run\n\texecutable p0\n\tworkingDirectory p1\n" +
                "\targuments concat s3 escapeArguments :\n\t\tconcat - s2 p2\n" +
                "\t\ts5\n\t\ts0\n" +
                "\tenvironment s1 concat :\n\t\ts6\n\t\tp2\n" +
                "\tenvironment s7 s8\n\tdescription concat - s6 p2\n" +
                "\tsemaphore s9 2 1\n\toutput p3\n");
            await Load(new[] { project });
            var process = m_operations.Single(operation => operation.AddProcess != null).AddProcess;
            string include = Absolute("include dir").ToString(m_paths);
            string expected = string.Join(" ", new[] { "-I" + include, "", payload }.Select(value => CommandLineEscaping.EscapeAsCommandLineWord(value)));
            Assert.Equal(expected, process.Arguments.ToString(m_paths));
            Assert.Equal(PipDataFragmentEscaping.EscapeEachArgument, process.Arguments.FragmentEscaping);
            Assert.Equal(string.Join(" ", new[] { "-I/mapped dir", "", payload }.Select(value => CommandLineEscaping.EscapeAsCommandLineWord(value))),
                process.Arguments.ToString(_ => "/mapped dir", m_paths.StringTable));
            Assert.Equal(2, process.EnvironmentVariables.Count);
            var environment = process.EnvironmentVariables.Single(variable => variable.Name.ToString(m_paths.StringTable) == "NAME");
            Assert.True(environment.Data.IsValid);
            Assert.False(environment.Value.IsValid);
            Assert.Equal("prefix:" + include, environment.Data.ToString(m_paths));
            Assert.Equal("$HOME \"quoted\"", process.EnvironmentVariables.Single(variable =>
                variable.Name.ToString(m_paths.StringTable) == "SCALAR").Data.ToString(m_paths));
            Assert.False(process.Description.IsValid);
            Assert.Equal("prefix:" + include, process.Usage.ToString(m_paths));
            Assert.Equal("prefix:/mapped dir", process.Usage.ToString(_ => "/mapped dir", m_paths.StringTable));
            Assert.Equal("lane", Assert.Single(process.Semaphores).Name.ToString(m_paths.StringTable));
            var write = m_operations.Single(operation => operation.AddWriteFile != null).AddWriteFile;
            Assert.Equal(payload + "\tstart\r\n\tend\n", write.Content.ToString(m_paths));
            using var rendered = new StringWriter();
            Assert.Equal(2, Renderer.Render(new StringReader(m_text[Assert.Single(project.GraphFiles)]), "p.dg", rendered));
            Assert.Contains("arguments:", rendered.ToString());
            Assert.Contains("escapeArguments", rendered.ToString());
            Assert.Contains("prefix:", rendered.ToString());
        }

        [Theory]
        [InlineData("concat s0 escapeArguments -", false)]
        [InlineData("concat s0 escapeArguments :", false)]
        [InlineData("concat s0 escapeArguments - s1", true)]
        [InlineData("concat s0 escapeArguments :\n\t\ts1", true)]
        public async Task CompactArgumentsDistinguishEmptyListsFromEmptyArguments(string expression, bool hasArgument)
        {
            var project = Project("p",
                "directgraph 3\npath 0 tool\npath 1 .\nstring 0  \nstring 1 \nstring 2 tool\n" +
                "writeFile tool\n\tdestination p0\n\tcontent s2\n" +
                "process run\n\texecutable p0\n\tworkingDirectory p1\n\targuments " + expression + "\n");
            await Load(new[] { project });
            var process = m_operations.Single(operation => operation.AddProcess != null).AddProcess;
            Assert.Equal(hasArgument ? CommandLineEscaping.EscapeAsCommandLineWord(string.Empty) : string.Empty,
                process.Arguments.ToString(m_paths));
            Assert.Equal(hasArgument ? 1 : 0, process.Arguments.FragmentCount);
        }

        [Fact]
        public void CompactDependencySetsPreserveOrderAndDuplicates()
        {
            var records = new DirectGraphTextReader(new StringReader(
                "directgraph 3\npath 0 first\npath 1 second\nset 0\n\tp1\n\tp0\n\tp1\n" +
                "process p\n\tinput @0\n"), "p.dg").Read().ToList();
            Assert.Equal(new[] { "second", "first", "second" }, Assert.Single(records).Fields["input"][0]);
        }

        [Theory]
        [InlineData("directgraph 3\r\n")]
        [InlineData("directgraph 3\nstring 0 value")]
        [InlineData("directgraph 3\nprocess p\n  executable $p0\n")]
        [InlineData("directgraph 3\nprocess  p\n")]
        [InlineData("directgraph 3\nstring 0 \0\n")]
        [InlineData("directgraph 3\nprocess p\n\targuments stringRef 9\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\targuments escapeArgument\n\t\tstringRef 0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\targuments concat :\n\t\t\ts0\n")]
        [InlineData("directgraph 3\nstring 0  \nprocess p\n\targuments concat s0 unknownMode :\n")]
        [InlineData("directgraph 3\nprocess p\n\targument\n\t\tliteral old field\n")]
        [InlineData("directgraph 3\nstring 0 value\nprocess p\n\targument $s0\n")]
        [InlineData("directgraph 3\nprocess p\r\n")]
        [InlineData("directgraph 3\nstring 0 value\nprocess p\n\targuments literal value\n")]
        [InlineData("directgraph 3\nprocess p\n\targuments concat\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\targuments concat : s0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\targuments concat - concat - s0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\targuments concat - s0\n\t\ts0\n")]
        [InlineData("directgraph 3\nprocess p\n\targuments concat - s99\n")]
        [InlineData("directgraph 3\npath 0 x\nprocess p\n\targuments concat p0 -\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\targuments concat - s0p0\n")]
        [InlineData("directgraph 3\nprocess p\n\targuments\n\t\tconcat -\n")]
        [InlineData("directgraph 3\nstring 0 x\nwriteFile w\n\tcontent\n\t\ts0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\tenvironment s0\n\t\ts0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\tenvironment s0 s0 s0\n")]
        [InlineData("directgraph 3\npath 0 x\nprocess p\n\texecutable $p0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\tpassthroughEnvironment $s0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\tenvironment $s0 s0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\tenvironment s0 $s0\n")]
        [InlineData("directgraph 3\nstring 0 x\nprocess p\n\toption $s0\n")]
        public void TabFramingRejectsMalformedInput(string text)
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                new DirectGraphTextReader(new StringReader(text), "invalid.dg").Read().ToList());
            Assert.Contains("invalid.dg:", error.Message);
        }

        [Fact]
        public async Task DuplicateArgumentsExpressionsAreRejected()
        {
            var project = Project("p",
                "directgraph 3\npath 0 tool\npath 1 .\nstring 0 tool\n" +
                "writeFile tool\n\tdestination p0\n\tcontent s0\n" +
                "process run\n\texecutable p0\n\tworkingDirectory p1\n" +
                "\targuments concat -\n\targuments concat -\n");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("one arguments expression", error.Message);
        }

        [Fact]
        public async Task DuplicateUsageExpressionsAreRejected()
        {
            var project = Project("p",
                "directgraph 3\npath 0 tool\npath 1 .\nstring 0 tool\n" +
                "writeFile tool\n\tdestination p0\n\tcontent s0\n" +
                "process run\n\texecutable p0\n\tworkingDirectory p1\n" +
                "\tdescription s0\n\tdescription concat - s0\n");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("description", error.Message);
            Assert.Contains("at most once", error.Message);
        }

        [Theory]
        [InlineData("directgraph 1\n")]
        [InlineData("directgraph 2\n")]
        [InlineData("directgraph 2\r\n")]
        [InlineData("directgraph 3\r\n")]
        [InlineData("directgraph 3")]
        [InlineData("directgraph 4\n")]
        public void ReaderRejectsPriorVersionsAndNoncanonicalHeaders(string header)
        {
            var error = Assert.Throws<InvalidDataException>(() =>
                new DirectGraphTextReader(new StringReader(header), "legacy.dg").Read().ToArray());
            Assert.Contains("legacy.dg:1:", error.Message);
            Assert.Contains("directgraph 3", error.Message);
        }

        private DirectGraphProjectSettings Project(string name, string text, params string[] dependencies)
        {
            var file = Absolute(name + ".dg");
            m_text.Add(file, DirectGraphFixtureFiles.NormalizeSourceLiteral(text));
            return new DirectGraphProjectSettings
            {
                Name = PathAtom.Create(m_paths.StringTable, name),
                Root = Absolute("."),
                GraphFiles = new[] { file },
                Dependencies = dependencies,
            };
        }

        private void AddPolicy(DirectGraphProjectSettings project, string text)
        {
            var file = Absolute(project.Name.ToString(m_paths.StringTable) + ".policy.dg");
            m_text.Add(file, DirectGraphFixtureFiles.NormalizeSourceLiteral(text));
            project.PolicyFiles = new[] { file };
        }

        private AbsolutePath Absolute(string value) => AbsolutePath.Create(m_paths, Path.GetFullPath(value, m_root));

        private async Task Load(
            IReadOnlyList<IDirectGraphProjectSettings> projects, string[] roots = null, Func<AbsolutePath, bool> probe = null,
            Action<CapturedOperation> reject = null, int maxConcurrency = 1,
            Action<CapturedOperation> beforeSend = null, Action<AbsolutePath> beforeOpen = null,
            Func<DirectGraphConstructionResult, DirectGraphConstructionResult> nativeResult = null)
        {
            var selected = DirectGraphProject.Create(projects, roots, m_paths);
            var constructor = new DirectGraphProjectGraphConstructor(m_paths, projects, state =>
                {
                    var core = new DirectGraphPipConstructor(
                        m_paths, m_pipGraph, new SandboxConfiguration(),
                        () => ProcessBuilder.CreateForTesting(m_paths, new NoOpCredentialScanner(), new LoggingContext("Test")),
                        TryGetHelper,
                        (string module, FileOrDirectoryArtifact artifact, out string error) => { error = null; return true; },
                        counters: null);
                    return new DirectGraphTextLoader(m_paths, core, state, (native, token) =>
                    {
                        var operation = new CapturedOperation(native);
                        beforeSend?.Invoke(operation);
                        lock (m_operations)
                        {
                            m_operations.Add(operation);
                        }
                        // TestPipGraph allocates IDs from its queue count, unlike the real concurrent PipGraph.Builder.
                        lock (m_pipGraph)
                        {
                            reject?.Invoke(operation);
                            var result = core.Construct(native);
                            return Task.FromResult(nativeResult == null ? result : nativeResult(result));
                        }
                    });
                },
                file =>
                {
                    beforeOpen?.Invoke(file);
                    lock (m_opened)
                    {
                        m_opened.Add(file);
                    }
                    return new StringReader(m_text[file]);
                },
                probe ?? (_ => true), CancellationToken.None);
            var result = await new ProjectGraphToGraphConstructor<DirectGraphProject>(constructor, maxConcurrency)
                .TryScheduleAsync(selected, m_context.QualifierTable.EmptyQualifierId);
            if (!result.Succeeded)
            {
                throw new InvalidDataException(result.Failure.Describe());
            }
            m_outputs = result.Result;
        }

        private bool TryGetHelper(string module, string symbol, out PipConstructionHelper helper, out string error)
        {
            if (!m_helpers.TryGetValue((module, symbol), out helper))
            {
                helper = PipConstructionHelper.CreateForTesting(
                    m_context, objectRoot: Absolute("."), redirectedRoot: Absolute("redirected"), pipGraph: m_pipGraph,
                    moduleName: module, symbol: symbol, specPath: Absolute(module + ".dsc"));
                m_helpers.Add((module, symbol), helper);
            }
            error = null;
            return true;
        }
    }
}
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
        private const string RenderFragment = """
            directgraph 2
            path 0 "out/a b"
            path 1 "out/dir"
            path 2 "out/result"
            string 0 "-c"
            string 1 "echo "
            string 2 "'literal'\r\n\nlast\rtail"
            string 3 ""
            string 4 "\n"
            set 0 $p0 $p0
            writeFile script
              destination $p0
              content $s1 $s2 $s4
            process run
              executable $p0
              argument $s0
              argument $s1 $s2
              argument $s3
              input @0
              output $p2
            sealDirectory dir
              directoryRoot $p1
              content $p0 $p2
            """;

        private readonly BuildXLContext m_context = BuildXLContext.CreateInstanceForTesting();
        private PathTable m_paths => m_context.PathTable;
        private readonly TestEnv.TestPipGraph m_pipGraph = new();
        private readonly Dictionary<(string Module, string Symbol), PipConstructionHelper> m_helpers = new();
        private readonly Dictionary<string, DirectGraphPipGraphBuilder> m_builders = new();
        private IReadOnlyDictionary<DirectGraphProject, ProjectGraphOutputs> m_outputs;
        private readonly string m_root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "directgraph-text-tests"));
        private readonly Dictionary<AbsolutePath, string> m_text = new();
        private readonly List<GraphOperation> m_operations = new();
        private readonly List<PipData> m_data = new();
        private readonly List<AbsolutePath> m_opened = new();

        [Fact]
        public void RendererExpandsChunksAndSetsButPreservesArgumentsPathsAndLineEndings()
        {
            using var output = new StringWriter();
            Assert.Equal(3, Renderer.Render(new StringReader(RenderFragment), "graph.dg", output));
            string view = output.ToString();
            Assert.Contains("not DG input or an executable script", view);
            Assert.Contains("source: \"graph.dg:", view);
            Assert.Contains("argument[0]: \"-c\"", view);
            Assert.Contains("argument[1]: block", view);
            Assert.Contains("argument[2]: \"\"", view);
            Assert.Contains("\"echo 'literal'\" + CRLF", view);
            Assert.Contains("\"\" + LF", view);
            Assert.Contains("\"last\" + CR", view);
            Assert.Contains("\"tail\" + EOF", view);
            Assert.Contains("\"tail\" + LF", view);
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
                File.WriteAllText(file, "directgraph 2\nwriteFile bad\n  content $s0");
                Assert.Equal(2, Renderer.Run(new[] { file }, output, error));
                Assert.Contains(file + ":3:", error.ToString());
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void ReaderStreamsAndPreservesEscapedLiteralsAndInternedValues()
        {
            using var reader = new StringReader("""
                directgraph 2
                path 0 "out/a b"
                path 1 "$literal"
                string 0 "$NOT_EXPANDED %HOME% \n\r\t\\\"\u0061"
                set 0 $p0 $p1
                process first
                  input @0
                  argument $s0
                unknown invalid
                """);
            using var records = new DirectGraphTextReader(reader, "graph.dg").Read().GetEnumerator();
            Assert.True(records.MoveNext());
            Assert.Equal(new[] { "out/a b", "$literal" }, records.Current.Fields["input"][0]);
            Assert.Equal("$NOT_EXPANDED %HOME% \n\r\t\\\"a", records.Current.Fields["argument"][0][0]);
            Assert.Throws<InvalidDataException>(() => records.MoveNext());
        }

        [Theory]
        [InlineData("\"\"", "")]
        [InlineData("\"plain caf\u00e9\"", "plain caf\u00e9")]
        [InlineData("\"prefix\\nmid\\u0061\\tend\"", "prefix\nmida\tend")]
        [InlineData("\"\\\"quoted\\\"\\\\\"", "\"quoted\"\\")]
        public void ReaderRetainsLiteralValuesAfterAdvancing(string literal, string expected)
        {
            using var reader = new StringReader($"directgraph 2\nstring 0 {literal}\nprocess first\n  argument $s0\n  option {literal}\nprocess second\n  argument $s0\n");
            using var records = new DirectGraphTextReader(reader, "graph.dg").Read().GetEnumerator();
            Assert.True(records.MoveNext());
            var first = records.Current;
            Assert.True(records.MoveNext());
            Assert.Equal("first", first.Name);
            Assert.Equal(expected, first.Fields["argument"][0][0]);
            Assert.Equal(expected, first.Fields["option"][0][0]);
            Assert.Equal(expected, records.Current.Fields["argument"][0][0]);
            Assert.False(records.MoveNext());
        }

        [Fact]
        public void ReaderReportsFragmentWhenUtf8DecodingFails()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("directgraph 2\nstring 0 \"").Concat(new byte[] { 0xff }).ToArray());
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            var error = Assert.Throws<InvalidDataException>(() => new DirectGraphTextReader(reader, "invalid.dg").Read().ToArray());
            Assert.Contains("invalid.dg", error.Message);
            Assert.Contains("Invalid UTF-8", error.Message);
            Assert.IsType<DecoderFallbackException>(error.InnerException);
        }

        [Theory]
        [InlineData("directgraph 1")]
        [InlineData("directgraph 2\nstring 0 \"x\"\nstring 0 \"y\"")]
        [InlineData("directgraph 2\npath 1 \"x\"")]
        [InlineData("directgraph 2\nstring 01 \"x\"")]
        [InlineData("directgraph 2\nstring +0 \"x\"")]
        [InlineData("directgraph 2\nstring -0 \"x\"")]
        [InlineData("directgraph 2\nstring 2147483648 \"x\"")]
        [InlineData("directgraph 2\nset 1")]
        [InlineData("directgraph 2\nset 0 @0")]
        [InlineData("directgraph 2\npath 0 \"x\"\nset 0 $p0\nprocess p\n  input @00")]
        [InlineData("directgraph 2\nwriteFile w\n  content $s0")]
        [InlineData("directgraph 2\nstring 0 \"x\"\nwriteFile w\n  destination $s0")]
        [InlineData("directgraph 2\npath 0 \"x\"\nwriteFile w\n  content $p0")]
        [InlineData("directgraph 2\nwriteFile w\n  content \"x\"")]
        [InlineData("directgraph 2\nwriteFile w\n  destination \"x\"")]
        [InlineData("directgraph 2\npath 0 \"x\"\nset 0 \"x\"")]
        [InlineData("directgraph 2\npath 0 \"x\"\nwriteFile w\n  destination $p01")]
        [InlineData("directgraph 2\npath 0 \"x\"\nwriteFile w\n  destination $p")]
        [InlineData("directgraph 2\nstring 0 \"bad\\q\"")]
        [InlineData("directgraph 2\nstring 0 \"bad\\u0000\"")]
        [InlineData("directgraph 2\nstring 0 \"x\"suffix")]
        [InlineData("directgraph 2\n  input $p0")]
        [InlineData("directgraph 2\nstring 0 \"x\"\nprocess p\n  argument $s0 $p0")]
        [InlineData("directgraph 2\nstring 0 \"x\"\nprocess p\n  argument $s0 \"y\"")]
        public void ReaderRejectsInvalidSyntax(string text) =>
            Assert.Throws<InvalidDataException>(() => new DirectGraphTextReader(new StringReader(text), "bad.dg").Read().ToArray());

        [Fact]
        public async Task FragmentLocalIdsRemapToDenseStreamRegistrations()
        {
            var project = Project("p", """
                directgraph 2
                path 0 "out/a"
                string 0 "first"
                writeFile first
                  destination $p0
                  content $s0
                """);
            AddPolicy(project, """
                directgraph 2
                path 0 "out/b"
                path 1 "out/a"
                path 2 "out/c"
                string 0 "second"
                writeFile second
                  destination $p0
                  content $s0
                copyFile copy
                  source $p1
                  destination $p2
                """);
            await Load(new[] { project });
            var paths = m_operations.SelectMany(operation => operation.NewPaths).ToArray();
            var strings = m_operations.SelectMany(operation => operation.NewStrings).ToArray();
            Assert.Equal(new[] { Absolute("out/a").ToString(m_paths), Absolute("out/b").ToString(m_paths), Absolute("out/c").ToString(m_paths) },
                paths.Select(registration => registration.Value));
            Assert.Equal(new uint[] { 0, 1, 2 }, paths.Select(registration => registration.Id));
            Assert.Equal(new[] { "first", "second" }, strings.Select(registration => registration.Value));
            Assert.Equal(new uint[] { 0, 1 }, strings.Select(registration => registration.Id));
            Assert.Equal(1UL, m_operations.Single(operation => operation.AddCopyFile != null).AddCopyFile.Source.ProducedFile.ProducerOperationId);
        }

        [Fact]
        public async Task ComposedArgumentsPreserveWhitespaceQuotesAndWholeArgumentEscaping()
        {
            var project = Project("p", """
                directgraph 2
                path 0 "tool"
                path 1 "."
                path 2 "out/result"
                string 0 "-c"
                string 1 "  echo\t"
                string 2 "'$HOME \"quoted\"'\\\n`backtick`''\"\""
                string 3 ""
                writeFile tool
                  destination $p0
                  content $s3
                process run
                  executable $p0
                  workingDirectory $p1
                  argument $s0
                  argument $s1 $s2 $s1
                  argument $s3 $s3
                  output $p2
                """);
            await Load(new[] { project });
            var operation = Assert.Single(m_operations.Where(operation => operation.AddProcess != null));
            Assert.Empty(operation.AddProcess.Arguments);
            var arguments = m_data[m_operations.IndexOf(operation)];
            var expected = PipDataBuilder.CreatePipData(m_paths.StringTable, " ", PipDataFragmentEscaping.CRuntimeArgumentRules,
                "-c", "  echo\t'$HOME \"quoted\"'\\\n`backtick`''\"\"  echo\t", "");
            Assert.Equal(expected.ToString(m_paths), arguments.ToString(m_paths));
            var groups = arguments.Select(fragment => fragment.GetNestedFragmentValue()).ToArray();
            Assert.Equal(3, groups.Length);
            Assert.True(groups[1].FragmentCount > 1);
            var chunks = groups[1].Select(fragment => fragment.GetStringIdValue().ToString(m_paths.StringTable)).ToArray();
            Assert.Contains("\"", chunks);
            Assert.Contains("'", chunks);
            Assert.Contains("`", chunks);
            Assert.Contains("backtick", chunks);
        }

        [Fact]
        public async Task ComposedContentPreservesRawBytesAndSharedChunks()
        {
            var project = Project("p", """
                directgraph 2
                path 0 "out/script.sh"
                string 0 "echo"
                string 1 " \t"
                string 2 "'\"`$HOME\\'\r\n\u00e9"
                writeFile script
                  destination $p0
                  content $s0 $s1 $s2 $s1 $s0
                """);
            await Load(new[] { project });
            var operation = Assert.Single(m_operations.Where(operation => operation.AddWriteFile != null));
            Assert.False(operation.AddWriteFile.HasContent);
            var content = m_data[m_operations.IndexOf(operation)];
            Assert.Equal("echo \t'\"`$HOME\\'\r\n\u00e9 \techo", content.ToString(m_paths));
            Assert.Equal(content.First().GetStringIdValue(), content.Last().GetStringIdValue());
        }

        [Theory]
        [InlineData("")]
        [InlineData("  content $s0\n  content $s0 $s0")]
        public async Task WriteFileRequiresExactlyOneContentField(string content)
        {
            var project = Project("p", "directgraph 2\npath 0 \"out/file\"\nstring 0 \"x\"\nwriteFile file\n  destination $p0\n" + content);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("Field 'content' must occur exactly once", error.Message);
        }

        [Fact]
        public async Task ConfiguredExportsBindInputsWithoutImportDeclarationsAndUnselectedFilesAreNotRead()
        {
            var producer = Project("producer", """
                directgraph 2
                path 0 "out/a"
                string 0 "a"
                writeFile write
                  destination $p0
                  content $s0
                """);
            producer.FileExports = new[] { Absolute("out/a") };
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "out/a"
                path 1 "out/b"
                copyFile copy
                  source $p0
                  destination $p1
                """, "producer");
            var ignored = Project("ignored", "not a graph");
            await Load(new[] { consumer, ignored, producer }, new[] { "consumer" });
            Assert.Equal(2, m_opened.Count);
            var copy = Assert.Single(m_operations.Where(operation => operation.AddCopyFile != null)).AddCopyFile;
            Assert.Equal(
                m_outputs.Single(pair => pair.Key.Name == "producer").Value.Files[Absolute("out/a")],
                m_builders["consumer"].GetProducedFile(copy.Source.ProducedFile.ProducerOperationId, Absolute("out/a"), out _));
            Assert.NotNull(m_operations.Last().Complete);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task PrivateAndNondependencyOutputsCannotBecomeStaleSources(bool dependency)
        {
            var producer = Project("producer", """
                directgraph 2
                path 0 "out/a"
                string 0 "a"
                writeFile write
                  destination $p0
                  content $s0
                """);
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "out/a"
                path 1 "out/b"
                copyFile copy
                  source $p0
                  destination $p1
                """, dependency ? new[] { "producer" } : Array.Empty<string>());
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { producer, consumer }));
            Assert.Contains("not exported", error.Message);
        }

        [Fact]
        public async Task ProjectLocalStreamsImportActualRewritesAndOrderDependencies()
        {
            var producer = Project("producer", """
                directgraph 2
                path 0 "out/a"
                path 1 "tools/tool"
                path 2 "."
                string 0 "data"
                writeFile first
                  destination $p0
                  content $s0
                writeFile tool
                  destination $p1
                  content $s0
                process rewrite
                  executable $p1
                  workingDirectory $p2
                  rewrite $p0
                """);
            producer.FileExports = new[] { Absolute("out/a"), Absolute("tools/tool") };
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "."
                path 2 "out/a"
                path 3 "out/result"
                process consume
                  executable $p0
                  workingDirectory $p1
                  input $p2
                  orderOnly $p2
                  output $p3
                """, "producer");
            await Load(new[] { consumer, producer });
            var processes = m_pipGraph.RetrieveScheduledPips().OfType<Process>().ToArray();
            var rewritten = Assert.Single(processes[0].FileOutputs).ToFileArtifact();
            Assert.Equal(2, rewritten.RewriteCount);
            Assert.Contains(rewritten, processes[1].Dependencies);
            Assert.Contains(processes[0].PipId, processes[1].OrderDependencies);
            Assert.NotSame(m_builders["producer"], m_builders["consumer"]);
            Assert.True(m_builders.Values.All(builder => builder.IsComplete));
            Assert.Equal(m_operations.First(operation => operation.AddWriteFile != null).OperationId,
                m_operations.Single(operation => operation.AddProcess?.Symbol == "consume").OperationId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TransitiveFilesRequireAnExplicitReexport(bool reexport)
        {
            var producer = Project("producer", """
                directgraph 2
                path 0 "out/a"
                string 0 "data"
                writeFile first
                  destination $p0
                  content $s0
                """);
            producer.FileExports = new[] { Absolute("out/a") };
            var forwarding = Project("forwarding", "directgraph 2", "producer");
            if (reexport)
            {
                forwarding.FileExports = producer.FileExports;
            }
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "out/a"
                path 1 "out/b"
                copyFile copy
                  source $p0
                  destination $p1
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
            var producer = Project("producer", """
                directgraph 2
                path 0 "out/include/a.h"
                path 1 "out/include"
                string 0 "header"
                writeFile write
                  destination $p0
                  content $s0
                sealDirectory headers
                  directoryRoot $p1
                  content $p0
                export public
                  directory $p1
                """);
            var forwarding = Project("forwarding", "directgraph 2", "producer");
            forwarding.DirectoryExports = new[] { Absolute("out/include") };
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "out/include"
                path 1 "out/include/a.h"
                path 2 "out/b.h"
                sealDirectory selected
                  directoryRoot $p0
                  sealKind full
                  content $p1
                  outputDirectory $p0 "producer/headers"
                copyFile copy
                  source $p1
                  destination $p2
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
            var producer = Project("producer", """
                directgraph 2
                path 0 "out/include/a.h"
                string 0 "header"
                writeFile write
                  destination $p0
                  content $s0
                """);
            AddPolicy(producer, """
                directgraph 2
                path 0 "out/include"
                path 1 "out/include/a.h"
                sealDirectory headers
                  directoryRoot $p0
                  content $p1
                export public
                  directory $p0
                """);
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "out/include/a.h"
                path 1 "out/b.h"
                copyFile copy
                  source $p0
                  destination $p1
                """, "producer");
            await Load(new[] { consumer, producer });
            var copy = Assert.Single(m_operations.Where(operation => operation.AddCopyFile != null)).AddCopyFile;
            Assert.Equal(
                m_outputs.Single(pair => pair.Key.Name == "producer").Value.Directories[Absolute("out/include")].Contents[0],
                m_builders["consumer"].GetProducedFile(copy.Source.ProducedFile.ProducerOperationId, Absolute("out/include/a.h"), out _));
            Assert.Equal(3UL, m_operations.Single(operation => operation.AddExport != null).OperationId);
        }

        [Fact]
        public async Task ExplicitSourceFilesDoNotImplicitlyImportSourceDirectoryGrants()
        {
            var sources = Project("sources", """
                directgraph 2
                path 0 "src"
                string 0 "*.h"
                sealDirectory source
                  directoryRoot $p0
                  sealKind sourceTopDirectoryOnly
                  pattern $s0
                export public
                  directory $p0
                """);
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "src/tool.h"
                path 1 "."
                path 2 "src/input.h"
                path 3 "out/compiled"
                process compile
                  executable $p0
                  workingDirectory $p1
                  orderOnly $p2
                  output $p3
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
            var sources = Project("sources", """
                directgraph 2
                path 0 "src"
                string 0 "*.h"
                string 1 ".git"
                sealDirectory source
                  directoryRoot $p0
                  sealKind sourceTopDirectoryOnly
                  pattern $s0
                  sourceExclusion $s1
                export public
                  directory $p0
                """);
            var consumer = Project("consumer", "directgraph 2\npath 0 \"" + path + "\"\npath 1 \"out/b\"\ncopyFile c\n  source $p0\n  destination $p1", "sources");
            await Load(new[] { consumer, sources });
            var copy = Assert.Single(m_operations.Where(operation => operation.AddCopyFile != null)).AddCopyFile;
            Assert.Equal(FileArtifactReference.ReferenceOneofCase.SourcePath, copy.Source.ReferenceCase);
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
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "out/input"
                path 1 "result/copy"
                copyFile read
                  source $p0
                  destination $p1
                """);
            var producer = Project("producer", directory ? """
                directgraph 2
                path 0 "tool"
                path 1 "."
                path 2 "out"
                process produce
                  executable $p0
                  workingDirectory $p1
                  outputDirectory $p2 shared
                """ : """
                directgraph 2
                path 0 "out/input"
                string 0 "produced"
                writeFile produce
                  destination $p0
                  content $s0
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
        public async Task SourceSealsCannotMaskGeneratedOutputRegionsInEitherLoadOrder(bool sourceFirst)
        {
            var source = Project("source", "directgraph 2\npath 0 \"out\"\nsealDirectory s\n  directoryRoot $p0\n  sealKind sourceAllDirectories");
            var generated = Project("generated", "directgraph 2\npath 0 \"out/a\"\nstring 0 \"a\"\nwriteFile w\n  destination $p0\n  content $s0");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(
                sourceFirst ? new[] { source, generated } : new[] { generated, source }));
            Assert.Contains("overlap", error.Message);
        }

        [Fact]
        public async Task GeneratedExecutablesAndSchedulingOnlySetsKeepTheirProducer()
        {
            var project = Project("p", """
                directgraph 2
                path 0 "out/tool"
                path 1 "."
                path 2 "out/result"
                string 0 "script"
                set 0 $p0
                writeFile tool
                  destination $p0
                  content $s0
                process use
                  executable $p0
                  workingDirectory $p1
                  orderOnly @0
                  output $p2
                """);
            await Load(new[] { project });
            var process = m_operations.Single(operation => operation.AddProcess != null).AddProcess;
            Assert.Equal(1UL, process.Executable.ProducedFile.ProducerOperationId);
            Assert.Equal(1UL, Assert.Single(process.OrderOnlyInputs).ProducedFile.ProducerOperationId);
        }

        [Theory]
        [InlineData("writeFile w\n  destination $p0\n  content $s0\n  typo \"x\"")]
        [InlineData("writeFile w\n  destination $p0\n  destination $p1\n  content $s0")]
        [InlineData("copyFile c\n  source $p1\n  destination $p0\n  typo x")]
        [InlineData("writeFile w\n  destination $p0\n  content $s0\nwriteFile w\n  destination $p1\n  content $s0")]
        public async Task SemanticErrorsHaveFragmentAndOperationContext(string body)
        {
            var project = Project("p", "directgraph 2\npath 0 \"out/a\"\npath 1 \"out/b\"\nstring 0 \"a\"\n" + body);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("p.dg:", error.Message);
            Assert.Contains("(p/", error.Message);
        }

        [Fact]
        public async Task EntireProjectDagIsValidatedBeforeOpeningAnyFragment()
        {
            var root = Project("root", "directgraph 2");
            var a = Project("a", "directgraph 2", "b");
            var b = Project("b", "directgraph 2", "a");
            await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { root, a, b }, new[] { "root" }));
            Assert.Empty(m_opened);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OverlappingOpaqueRootsRequireAnExplicitDirectoryIdentity(bool explicitIdentity)
        {
            var project = Project("p", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "."
                path 2 "out/shared"
                path 3 "out/shared/member"
                path 4 "out/result"
                string 0 "executable"
                writeFile tool
                  destination $p0
                  content $s0
                process left
                  executable $p0
                  workingDirectory $p1
                  outputDirectory $p2
                process right
                  executable $p0
                  workingDirectory $p1
                  outputDirectory $p2
                process consume
                  executable $p0
                  workingDirectory $p1
                  input $p3
                  output $p4
                """ + (explicitIdentity ? "\n  inputDirectory $p2 left" : ""));
            if (!explicitIdentity)
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
                Assert.Contains("unambiguous directory", error.Message);
                return;
            }
            await Load(new[] { project });
            var consumer = m_operations.Single(operation => operation.AddProcess?.Symbol == "consume").AddProcess;
            Assert.Equal(2UL, Assert.Single(consumer.InputDirectories).ProducerOperationId);
            Assert.Empty(consumer.InputFiles);
        }

        [Fact]
        public async Task KnownOpaqueMembersKeepTheirProducedFileArtifacts()
        {
            var producer = Project("producer", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "."
                path 2 "out/opaque"
                path 3 "out/opaque/member"
                string 0 "tool"
                writeFile tool
                  destination $p0
                  content $s0
                process generate
                  executable $p0
                  workingDirectory $p1
                  output $p3
                  outputDirectory $p2
                """);
            producer.FileExports = new[] { Absolute("tools/tool") };
            producer.DirectoryExports = new[] { Absolute("out/opaque") };
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "."
                path 2 "out/opaque/member"
                path 3 "out/result"
                process consume
                  executable $p0
                  workingDirectory $p1
                  input $p2
                  output $p3
                """, "producer");
            await Load(new[] { consumer, producer });
            var request = m_operations.Single(operation => operation.AddProcess?.Symbol == "consume").AddProcess;
            var reference = Assert.Single(request.InputFiles);
            Assert.Equal(FileArtifactReference.ReferenceOneofCase.ProducedFile, reference.ReferenceCase);
            var processes = m_pipGraph.RetrieveScheduledPips().OfType<Process>().ToArray();
            Assert.Contains(Assert.Single(processes[0].FileOutputs).ToFileArtifact(), processes[1].Dependencies);
            Assert.Single(request.InputDirectories);
        }

        [Fact]
        public async Task UnlistedOpaqueExecutablesRequireConcreteArtifacts()
        {
            var project = Project("p", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "."
                path 2 "out/opaque"
                path 3 "out/opaque/executable"
                string 0 "tool"
                writeFile tool
                  destination $p0
                  content $s0
                process generate
                  executable $p0
                  workingDirectory $p1
                  outputDirectory $p2
                process consume
                  executable $p3
                  workingDirectory $p1
                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("concrete file artifact", error.Message);
        }

        [Fact]
        public async Task OpaqueExportsDoNotExposeUnrelatedPrivateExplicitOutputs()
        {
            var producer = Project("producer", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "out/opaque/private"
                path 2 "."
                path 3 "out/opaque"
                string 0 "executable"
                string 1 "private"
                writeFile tool
                  destination $p0
                  content $s0
                writeFile private
                  destination $p1
                  content $s1
                process opaque
                  executable $p0
                  workingDirectory $p2
                  outputDirectory $p3
                """);
            producer.FileExports = new[] { Absolute("tools/tool") };
            producer.DirectoryExports = new[] { Absolute("out/opaque") };
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "."
                path 2 "out/opaque/private"
                path 3 "out/result"
                process consume
                  executable $p0
                  workingDirectory $p1
                  input $p2
                  output $p3
                """, "producer");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { consumer, producer }));
            Assert.Contains("private explicit file", error.Message);
        }

        [Fact]
        public async Task BuilderRejectionsStopConstructionBeforeCompletion()
        {
            var project = Project("p", """
                directgraph 2
                path 0 "out/a"
                path 1 "out/b"
                string 0 "a"
                string 1 "b"
                writeFile rejected
                  destination $p0
                  content $s0
                writeFile neverRead
                  destination $p1
                  content $s1
                """);
            int sent = 0;
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }, reject: operation =>
            {
                sent++;
                return new GraphOperationResponse
                {
                    Status = GraphOperationResponseStatus.GraphError,
                    Error = "pip validation rejected",
                };
            }));
            Assert.Contains("pip validation rejected", error.Message);
            Assert.Equal(1, sent);
        }

        [Fact]
        public async Task OpaqueExportsDoNotExposeOtherProjectsPrivateFilesWithMatchingLocalIds()
        {
            var unrelated = Project("unrelated", """
                directgraph 2
                path 0 "out/other"
                path 1 "out/opaque/private"
                string 0 "private"
                writeFile first
                  destination $p0
                  content $s0
                writeFile private
                  destination $p1
                  content $s0
                """);
            var producer = Project("producer", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "."
                path 2 "out/opaque"
                string 0 "tool"
                writeFile tool
                  destination $p0
                  content $s0
                process opaque
                  executable $p0
                  workingDirectory $p1
                  outputDirectory $p2
                """, "unrelated");
            producer.FileExports = new[] { Absolute("tools/tool") };
            producer.DirectoryExports = new[] { Absolute("out/opaque") };
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "tools/tool"
                path 1 "."
                path 2 "out/opaque/private"
                path 3 "out/result"
                process consume
                  executable $p0
                  workingDirectory $p1
                  input $p2
                  output $p3
                """, "producer");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { consumer, producer, unrelated }));
            Assert.Contains("private explicit file", error.Message);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task OpaqueMemberChecksDoNotDependOnSiblingFileClaimOrder(bool readerFirst)
        {
            const string header = "directgraph 2\npath 0 \"tool\"\npath 1 \".\"\npath 2 \"out/opaque\"\npath 3 \"out/opaque/private\"\npath 4 \"out/result\"\nstring 0 \"content\"\n";
            var provider = Project("provider", header +
                "process opaque\n  executable $p0\n  workingDirectory $p1\n  outputDirectory $p2 shared\nexport publish\n  directory $p2\n");
            var owner = Project("owner", header + "writeFile private\n  destination $p3\n  content $s0\n");
            var consumer = Project("consumer", header +
                "process read\n  executable $p0\n  workingDirectory $p1\n  input $p3\n  output $p4\n", "provider");
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
            var selected = Project("selected", """
                directgraph 2
                path 0 "src"
                sealDirectory source
                  directoryRoot $p0
                  sealKind sourceAllDirectories
                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { ignored, selected }, new[] { "selected" }));
            Assert.Contains("overlaps generated outputs", error.Message);
            Assert.Equal(new[] { Absolute("selected.dg") }, m_opened);
        }

        [Theory]
        [InlineData("out/a")]
        [InlineData("out/./a")]
        public async Task DuplicateSealMembersReportRecordContext(string second)
        {
            var project = Project("duplicate", $$"""
                directgraph 2
                path 0 "out/a"
                path 1 "out"
                path 2 "{{second}}"
                string 0 "content"
                writeFile first
                  destination $p0
                  content $s0
                sealDirectory headers
                  directoryRoot $p1
                  content $p0
                  content $p2
                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("duplicate.dg:9", error.Message);
            Assert.Contains("duplicate/headers", error.Message);
            Assert.Contains("Duplicate seal content", error.Message);
        }

        [Fact]
        public async Task TempAndSemaphoreFieldsUseNativeConstruction()
        {
            var project = Project("p", """
                directgraph 2
                path 0 "tool"
                path 1 "."
                path 2 "out/one"
                path 3 "out/two"
                path 4 "scratch"
                path 5 "out/three"
                path 6 "out/four"
                string 0 "mutex"
                writeFile tool
                  destination $p0
                  content $s0
                process one
                  executable $p0
                  workingDirectory $p1
                  output $p2
                  enableTempDirectory true
                  semaphore $s0 1 1
                process two
                  executable $p0
                  workingDirectory $p1
                  output $p3
                  tempDirectory $p4
                  semaphore $s0 1 1
                process three
                  executable $p0
                  workingDirectory $p1
                  output $p5
                  enableTempDirectory true
                  semaphore $s0 1 1
                process four
                  executable $p0
                  workingDirectory $p1
                  output $p6
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
        [InlineData("enableTempDirectory true\n  tempDirectory $p1")]
        [InlineData("enableTempDirectory maybe")]
        [InlineData("semaphore $s0 0 1")]
        [InlineData("semaphore $s0 1 2")]
        [InlineData("semaphore $s0 1 1\n  semaphore $s0 1 1")]
        public async Task InvalidTempAndSemaphoreFieldsFailWithRecordContext(string fields)
        {
            var project = Project("p", "directgraph 2\npath 0 \"tool\"\npath 1 \".\"\npath 2 \"out\"\nstring 0 \"mutex\"\nwriteFile tool\n  destination $p0\n  content $s0\nprocess run\n  executable $p0\n  workingDirectory $p1\n  output $p2\n  " + fields);
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
            string header = $"directgraph 2\npath 0 \"tool\"\npath 1 \".\"\npath 2 \"scratch\"\npath 3 \"{secondTemp}\"\npath 4 \"out/one\"\npath 5 \"out/two\"\n";
            string process(string name, int temp, int output) =>
                $"process {name}\n  executable $p0\n  workingDirectory $p1\n  tempDirectory $p{temp}\n  output $p{output}\n";
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
            string header = $"directgraph 2\npath 0 \"tool\"\npath 1 \".\"\npath 2 \"{output}\"\npath 3 \"{temp}\"\npath 4 \"elsewhere/result\"\nstring 0 \"content\"\n";
            const string scratch = "process scratch\n  executable $p0\n  workingDirectory $p1\n  tempDirectory $p3\n  output $p4\n";
            string generated = directory
                ? "process generated\n  executable $p0\n  workingDirectory $p1\n  outputDirectory $p2 shared\n"
                : "writeFile generated\n  destination $p2\n  content $s0\n";
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
            var project = Project("p", """
                directgraph 2
                path 0 "src"
                path 1 "src/.git/HEAD"
                path 2 "."
                path 3 "out/result"
                string 0 ".git"
                string 1 "nested/private"
                sealDirectory source
                  directoryRoot $p0
                  sealKind sourceAllDirectories
                  sourceExclusion $s0
                  sourceExclusion $s1
                process read
                  executable $p1
                  workingDirectory $p2
                  input $p1
                  output $p3
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
            var project = Project("p", $"directgraph 2\npath 0 \"src\"\nstring 0 \"{exclusion}\"\nsealDirectory s\n  directoryRoot $p0\n  sealKind {kind}\n  sourceExclusion $s0");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("p/s", error.Message);
            Assert.Contains("exclusion", error.Message);
        }

        [Fact]
        public async Task ExplicitSourcesWorkWithoutAnySourceSeal()
        {
            var project = Project("p", "directgraph 2\npath 0 \"source\"\npath 1 \"out\"\ncopyFile copy\n  source $p0\n  destination $p1");
            await Load(new[] { project });
            Assert.True(Assert.Single(m_pipGraph.RetrieveScheduledPips().OfType<CopyFile>()).Source.IsSourceFile);
        }

        [Fact]
        public async Task ConfigurationAndFragmentExportsMergeIdenticalArtifactsBeforeSending()
        {
            var project = Project("p", """
                directgraph 2
                path 0 "out/file"
                path 1 "out"
                string 0 "content"
                writeFile file
                  destination $p0
                  content $s0
                sealDirectory seal
                  directoryRoot $p1
                  content $p0
                export first
                  file $p0
                  file $p0
                  directory $p1
                export again
                  file $p0
                  directory $p1 seal
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
            var project = Project("p", """
                directgraph 2
                path 0 "out"
                path 1 "tool"
                path 2 "."
                process first
                  executable $p1
                  workingDirectory $p2
                  outputDirectory $p0 shared
                process second
                  executable $p1
                  workingDirectory $p2
                  outputDirectory $p0 shared
                export one
                  directory $p0 first
                export two
                  directory $p0 second
                """);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { project }));
            Assert.Contains("Conflicting directory export identities", error.Message);
        }

        [Fact]
        public async Task IndependentProjectsConstructConcurrentlyWithinTheBound()
        {
            var projects = Enumerable.Range(0, 3).Select(index => Project("p" + index,
                $"directgraph 2\npath 0 \"out/{index}\"\nstring 0 \"value{index}\"\nwriteFile write\n  destination $p0\n  content $s0")).ToArray();
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
            var provider = Project("provider", """
                directgraph 2
                path 0 "out/include/file"
                path 1 "out/include"
                string 0 "content"
                writeFile write
                  destination $p0
                  content $s0
                sealDirectory seal
                  directoryRoot $p1
                  content $p0
                export publish
                  file $p0
                  directory $p1
                """);
            var consumer = Project("consumer", """
                directgraph 2
                path 0 "out/include/file"
                path 1 "out/include"
                path 2 "tool"
                path 3 "."
                path 4 "out/result"
                process use
                  executable $p2
                  workingDirectory $p3
                  input $p0
                  inputDirectory $p1 provider/seal
                  output $p4
                """, "provider");
            var other = Project("other", "directgraph 2\npath 0 \"out/other\"\nstring 0 \"other\"\nwriteFile write\n  destination $p0\n  content $s0");
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
                            Assert.True(m_builders["provider"].IsComplete);
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

        [Fact]
        public async Task FailedProjectDoesNotStartDependents()
        {
            var failed = Project("failed", "directgraph 2\npath 0 \"out/bad\"\nstring 0 \"bad\"\nwriteFile bad\n  destination $p0\n  content $s0\n  unsupported true");
            var dependent = Project("dependent", "directgraph 2", "failed");
            var unrelated = Project("unrelated", "directgraph 2");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                Load(new[] { dependent, unrelated, failed }, maxConcurrency: 3));
            Assert.Contains("failed/bad", error.Message);
            Assert.DoesNotContain(Absolute("dependent.dg"), m_opened);
        }

        private DirectGraphProjectSettings Project(string name, string text, params string[] dependencies)
        {
            var file = Absolute(name + ".dg");
            m_text.Add(file, text);
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
            m_text.Add(file, text);
            project.PolicyFiles = new[] { file };
        }

        private AbsolutePath Absolute(string value) => AbsolutePath.Create(m_paths, Path.GetFullPath(value, m_root));

        private async Task Load(
            IReadOnlyList<IDirectGraphProjectSettings> projects, string[] roots = null, Func<AbsolutePath, bool> probe = null,
            Func<GraphOperation, GraphOperationResponse> reject = null, int maxConcurrency = 1,
            Action<GraphOperation> beforeSend = null, Action<AbsolutePath> beforeOpen = null)
        {
            var selected = DirectGraphProject.Create(projects, roots, m_paths);
            var constructor = new DirectGraphProjectGraphConstructor(m_paths, projects, state =>
                {
                    var builder = new DirectGraphPipGraphBuilder(
                        m_paths, m_pipGraph, new SandboxConfiguration(),
                        () => ProcessBuilder.CreateForTesting(m_paths, new NoOpCredentialScanner(), new LoggingContext("Test")),
                        TryGetHelper,
                        (string module, FileOrDirectoryArtifact artifact, out string error) => { error = null; return true; },
                        new LoggingContext("Test"), traceOperation: false);
                    return new DirectGraphTextLoader(m_paths, builder, state, (operation, data, token) =>
                    {
                        beforeSend?.Invoke(operation);
                        string module = operation.AddProcess?.Module ?? operation.AddWriteFile?.Module ?? operation.AddCopyFile?.Module ??
                            operation.AddSealDirectory?.Module ?? operation.AddCompositeSharedOpaqueDirectory?.Module ?? operation.AddExport?.Module;
                        lock (m_operations)
                        {
                            if (module != null)
                            {
                                m_builders[module] = builder;
                            }
                            m_operations.Add(operation);
                            m_data.Add(data);
                        }
                        // TestPipGraph allocates IDs from its queue count, unlike the real concurrent PipGraph.Builder.
                        lock (m_pipGraph)
                        {
                            return reject == null ? builder.HandleOperationAsync(operation, data, token) : Task.FromResult(reject(operation));
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
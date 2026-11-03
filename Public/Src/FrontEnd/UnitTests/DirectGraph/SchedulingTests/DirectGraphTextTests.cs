// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuildXL.Engine.DirectGraph;
using BuildXL.FrontEnd.DirectGraph;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Configuration;
using BuildXL.Utilities.Configuration.Mutable;
using BuildXL.Utilities.Core;
using GraphRenderer;
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

        private readonly PathTable m_paths = new();
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
            Assert.Equal(1UL, copy.Source.ProducedFile.ProducerOperationId);
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
            Assert.Equal(1UL, copy.Source.ProducedFile.ProducerOperationId);
            Assert.Equal(3UL, m_operations.Single(operation => operation.AddExport != null).OperationId);
        }

        [Fact]
        public async Task SourceProvidersAuthorizeOnlyTheirVisibleScopeAndOrderOnlySourcesAreProbed()
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
            Assert.Single(process.InputDirectories);
        }

        [Theory]
        [InlineData("src/sub/input.h")]
        [InlineData("src/input.c")]
        [InlineData("other/input.h")]
        public async Task UnresolvedInputsRequireMatchingSourceAuthorization(string path)
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
            var consumer = Project("consumer", "directgraph 2\npath 0 \"" + path + "\"\npath 1 \"out/b\"\ncopyFile c\n  source $p0\n  destination $p1", "sources");
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => Load(new[] { consumer, sources }));
            Assert.Contains("explicit source seal", error.Message);
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
        [InlineData("copyFile c\n  source $p1\n  destination $p0")]
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
            var loader = new DirectGraphTextLoader(m_paths, (operation, data, token) =>
            {
                sent++;
                return Task.FromResult(new GraphOperationResponse
                {
                    Status = GraphOperationResponseStatus.GraphError,
                    Error = "pip validation rejected",
                });
            });
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(new[] { project }, null,
                file => new StringReader(m_text[file]), _ => true, CancellationToken.None));
            Assert.Contains("pip validation rejected", error.Message);
            Assert.Equal(1, sent);
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

        private Task Load(IReadOnlyList<IDirectGraphProjectSettings> projects, string[] roots = null, Func<AbsolutePath, bool> probe = null)
        {
            var loader = new DirectGraphTextLoader(m_paths, (operation, data, token) =>
            {
                m_operations.Add(operation);
                m_data.Add(data);
                return Task.FromResult(new GraphOperationResponse { Status = GraphOperationResponseStatus.Success });
            });
            return loader.LoadAsync(projects, roots,
                file => { m_opened.Add(file); return new StringReader(m_text[file]); },
                probe ?? (_ => true), CancellationToken.None);
        }
    }
}
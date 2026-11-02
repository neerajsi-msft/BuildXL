// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
using GraphEmitter;
using Newtonsoft.Json.Linq;
using Test.BuildXL.TestUtilities;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;

namespace Test.BuildXL.FrontEnd.DirectGraph
{
    public sealed class ProjectGraphDefinitionTests : XunitBuildXLTest
    {
        public ProjectGraphDefinitionTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void LinksSelectedClosureAndNamespacesLocalProducers()
        {
            using var files = new TempFileStorage(canGetFileNames: true);
            string root = files.GetDirectory("fragments");
            Write(root, "a.json", Fragment("""
                [{"kind":"writeFile","name":"write","destination":"sdk/a.txt","content":"hello"}]
                """, """{"files":[{"producedBy":"write","path":"sdk/a.txt"}]}"""));
            Write(root, "b.json", Fragment("""
                [{"kind":"copyFile","name":"write","destination":"out/b.txt","source":{"sourcePath":"sdk/a.txt"}}]
                """, """{"files":[{"producedBy":"write","path":"out/b.txt"}]}"""));
            Write(root, "unused.json", Fragment("[]"));
            string manifest = Write(root, "projects.json", """
                {"formatVersion":1,"roots":["b"],"projects":[
                    {"name":"b","fragment":"b.json","dependencies":["a"]},
                    {"name":"unused","fragment":"unused.json","dependencies":[]},
                    {"name":"a","fragment":"a.json","dependencies":[]}]}
                """);
            var graph = ProjectGraphDefinition.Load(manifest);
            Assert.Equal(new[] { "a", "b" }, graph.Modules.Select(module => module.Name));
            var producer = graph.Modules[0].Operations[0];
            var consumer = graph.Modules[1].Operations[0];
            Assert.NotEqual((string)producer["name"], (string)consumer["name"]);
            Assert.Equal((string)producer["name"], (string)consumer["source"]["producedBy"]);
            Assert.Equal(Path.Combine(root, "sdk", "a.txt"), (string)consumer["source"]["path"]);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PrivateAndTransitiveOutputsCannotBecomeStaleSources(bool transitive)
        {
            using var files = new TempFileStorage(canGetFileNames: true);
            string root = files.GetDirectory("fragments");
            Write(root, "a.json", Fragment("""[{"kind":"writeFile","name":"write","destination":"hidden.txt","content":"fresh"}]"""));
            Write(root, "bridge.json", Fragment("[]"));
            Write(root, "hidden.txt", "stale");
            Write(root, "b.json", Fragment("""[{"kind":"copyFile","name":"copy","destination":"b.txt","source":{"sourcePath":"hidden.txt"}}]"""));
            string manifest = Write(root, "projects.json", $$"""
                {"formatVersion":1,"projects":[
                    {"name":"a","fragment":"a.json","dependencies":[]},
                    {"name":"bridge","fragment":"bridge.json","dependencies":["a"]},
                    {"name":"b","fragment":"b.json","dependencies":["{{(transitive ? "bridge" : "a")}}"]}]}
                """);
            string error = Assert.Throws<InvalidDataException>(() => ProjectGraphDefinition.Load(manifest)).Message;
            Assert.Contains(transitive ? "nondependency" : "does not export", error);
        }

        [Fact]
        public void ProjectCyclesAreRejectedBeforeLoadingFragments()
        {
            using var files = new TempFileStorage(canGetFileNames: true);
            string manifest = Write(files.GetDirectory("fragments"), "projects.json", """
                {"formatVersion":1,"projects":[
                    {"name":"a","fragment":"missing.json","dependencies":["b"]},
                    {"name":"b","fragment":"missing.json","dependencies":["a"]}]}
                """);
            Assert.Contains("cycle", Assert.Throws<InvalidDataException>(() => ProjectGraphDefinition.Load(manifest)).Message);
        }

        [Fact]
        public void PartialDirectoryMembersRetainTheirOriginalFileProducer()
        {
            using var files = new TempFileStorage(canGetFileNames: true);
            string root = files.GetDirectory("fragments");
            Write(root, "a.json", Fragment("""
                [{"kind":"writeFile","name":"write","destination":"sdk/a.txt","content":"hello"},
                 {"kind":"sealDirectory","name":"seal","directoryRoot":"sdk","contents":[{"producedBy":"write","path":"sdk/a.txt"}]}]
                """, """{"directories":[{"producedBy":"seal","path":"sdk"}]}"""));
            Write(root, "b.json", Fragment("""[{"kind":"copyFile","name":"copy","destination":"b.txt","source":{"sourcePath":"sdk/a.txt"}}]"""));
            string manifest = Write(root, "projects.json", """
                {"formatVersion":1,"projects":[
                    {"name":"b","fragment":"b.json","dependencies":["a"]},
                    {"name":"a","fragment":"a.json","dependencies":[]}]}
                """);
            var graph = ProjectGraphDefinition.Load(manifest);
            Assert.Equal((string)graph.Modules[0].Operations[0]["name"], (string)graph.Modules[1].Operations[0]["source"]["producedBy"]);
        }

        [Fact]
        public void SharedSetsExpandWithoutChangingDependencyKindOrLiteralArguments()
        {
            using var files = new TempFileStorage(canGetFileNames: true);
            string root = files.GetDirectory("fragments");
            string shell = Path.Combine(root, "shell");
            string fragment = $$"""
                {"formatVersion":1,"dependencySets":{"generated":[{"producedBy":"write","path":"a.txt"}]},
                 "operations":[
                    {"kind":"writeFile","name":"write","destination":"a.txt","content":"hello"},
                    {"kind":"process","name":"run","executable":{{Newtonsoft.Json.JsonConvert.SerializeObject(shell)}},"workingDirectory":".",
                     "arguments":["%DO_NOT_EXPAND%","$literal"],"orderOnlySets":["generated"],"outputFiles":["b.txt"]}]}
                """;
            Write(root, "a.json", fragment);
            var graph = ProjectGraphDefinition.Load(Write(root, "projects.json", """
                {"formatVersion":1,"projects":[{"name":"a","fragment":"a.json","dependencies":[]}]}
                """));
            var process = graph.Modules[0].Operations[1];
            Assert.Empty((JArray)process["inputFiles"]);
            Assert.Single((JArray)process["orderOnlyInputs"]);
            Assert.Equal("%DO_NOT_EXPAND%", (string)process["arguments"][0]);
            Assert.Equal((string)graph.Modules[0].Operations[0]["name"], (string)process["orderOnlyInputs"][0]["producedBy"]);
        }

        [Theory]
        [InlineData("""{"producedBy":"invalid"}""")]
        [InlineData("""{"dependency":"a"}""")]
        [InlineData("""{"path":"sdk/member.txt"}""")]
        public void OpaqueDirectoryBindingDoesNotAcceptMixedFileReferenceShapes(string additionalFields)
        {
            using var files = new TempFileStorage(canGetFileNames: true);
            string root = files.GetDirectory("fragments");
            string shell = Newtonsoft.Json.JsonConvert.SerializeObject(Path.Combine(root, "shell"));
            Write(root, "a.json", Fragment($$"""
                [{"kind":"process","name":"generate","executable":{{shell}},"workingDirectory":".","producesDirectories":["sdk"]}]
                """, """{"directories":[{"producedBy":"generate","path":"sdk"}]}"""));
            var reference = JObject.Parse(additionalFields);
            reference["sourcePath"] = "sdk/member.txt";
            Write(root, "b.json", Fragment($$"""
                [{"kind":"process","name":"consume","executable":{{shell}},"workingDirectory":".","inputFiles":[{{reference}}],"outputFiles":["b.txt"]}]
                """));
            string manifest = Write(root, "projects.json", """
                {"formatVersion":1,"projects":[
                    {"name":"a","fragment":"a.json","dependencies":[]},
                    {"name":"b","fragment":"b.json","dependencies":["a"]}]}
                """);
            Assert.Throws<InvalidDataException>(() => ProjectGraphDefinition.Load(manifest));
        }

        [Fact]
        public void GeneratedExecutableBindsToDirectDependencyExport()
        {
            using var files = new TempFileStorage(canGetFileNames: true);
            string root = files.GetDirectory("fragments");
            Write(root, "a.json", Fragment("""
                [{"kind":"writeFile","name":"write","destination":"tool","content":"executable"}]
                """, """{"files":[{"producedBy":"write","path":"tool"}]}"""));
            Write(root, "b.json", Fragment("""
                [{"kind":"process","name":"run","executableFile":{"dependency":"a","path":"tool"},"workingDirectory":".","outputFiles":["b.txt"]}]
                """));
            var graph = ProjectGraphDefinition.Load(Write(root, "projects.json", """
                {"formatVersion":1,"projects":[
                    {"name":"a","fragment":"a.json","dependencies":[]},
                    {"name":"b","fragment":"b.json","dependencies":["a"]}]}
                """));
            Assert.Equal((string)graph.Modules[0].Operations[0]["name"],
                (string)graph.Modules[1].Operations[0]["executableFile"]["producedBy"]);
        }

        [Theory]
        [InlineData("""{"formatVersion":2,"projects":[]}""", typeof(InvalidDataException))]
        [InlineData("""{"formatVersion":1,"formatVersion":1,"projects":[]}""", typeof(Newtonsoft.Json.JsonReaderException))]
        [InlineData("""{"formatVersion":1,"projects":[],"unknown":true}""", typeof(Newtonsoft.Json.JsonSerializationException))]
        [InlineData("""{"formatVersion":1,"projects":[{"name":"a","fragment":"a.json","dependencies":["missing"]}]}""", typeof(InvalidDataException))]
        public void InvalidManifestsFailExplicitly(string json, Type exceptionType)
        {
            using var files = new TempFileStorage(canGetFileNames: true);
            string manifest = Write(files.GetDirectory("fragments"), "projects.json", json);
            Assert.Throws(exceptionType, () => ProjectGraphDefinition.Load(manifest));
        }

        private static string Fragment(string operations, string exports = "{}") =>
            $$"""{"formatVersion":1,"operations":{{operations}},"exports":{{exports}}}""";

        private static string Write(string root, string name, string text)
        {
            string path = Path.Combine(root, name);
            File.WriteAllText(path, text);
            return path;
        }
    }
}
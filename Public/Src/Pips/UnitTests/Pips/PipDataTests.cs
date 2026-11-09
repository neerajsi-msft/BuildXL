// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.CLI;
using BuildXL.Utilities.Core;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;

namespace Test.BuildXL.Pips
{
    public sealed class PipDataTests : XunitBuildXLTest
    {
        private readonly PathTable m_pathTable;
        private readonly AbsolutePath m_uniqueEntry0;
        private StringId m_expectedStringId0;
        private const string ExpectedString0 = "expectedString0";
        private const string CaseVaryingString = "CASE_varying_String";
        private readonly string m_separator0 = string.Empty;
        private const string Separator1 = "\n";
        private const PipDataFragmentEscaping Escaping0 = PipDataFragmentEscaping.CRuntimeArgumentRules;
        private const PipDataFragmentEscaping Escaping1 = PipDataFragmentEscaping.NoEscaping;
        private const string UniqueEntry1 = "unique to fragment 0";
        private readonly PipDataBuilder m_pipDataBuilder;
        private const PipDataFragmentEscaping EnclosingEscaping = PipDataFragmentEscaping.CRuntimeArgumentRules;
        private const string EnclosingSeparator = " #S ";
        private readonly PipDataBuilder.Cursor m_cursorStart;
        private readonly PipDataBuilder.Cursor m_cursor0;
        private readonly PipDataBuilder.Cursor m_cursor1;
        private readonly PipDataBuilder.Cursor m_cursorEnd;

        public PipDataTests(ITestOutputHelper output)
            : base(output)
        {
            m_pathTable = new PathTable();
            m_expectedStringId0 = StringId.Create(m_pathTable.StringTable, ExpectedString0);
            m_pipDataBuilder = new PipDataBuilder(m_pathTable.StringTable);

            m_expectedStringId0 = StringId.Create(m_pathTable.StringTable, ExpectedString0);
            m_uniqueEntry0 = AbsolutePath.Create(m_pathTable, A("c", "unique to fragment 0"));

            // BEGIN ADDING ARGUMENTS
            m_cursorStart = m_pipDataBuilder.CreateCursor();

            AddStandardBlock(m_pipDataBuilder);

            m_cursor0 = m_pipDataBuilder.CreateCursor();

            using (m_pipDataBuilder.StartFragment(Escaping0, m_separator0))
            {
                m_pipDataBuilder.Add(m_uniqueEntry0);
                AddStandardBlock(m_pipDataBuilder);
            }

            m_cursor1 = m_pipDataBuilder.CreateCursor();

            using (m_pipDataBuilder.StartFragment(Escaping1, Separator1))
            {
                AddStandardBlock(m_pipDataBuilder);
                m_pipDataBuilder.Add(UniqueEntry1);
            }

            m_cursorEnd = m_pipDataBuilder.CreateCursor();

            // END ADDING ARGUMENTS
        }

        [Fact]
        public void PipDataReadWrite()
        {
            var pipData = m_pipDataBuilder.ToPipData(EnclosingSeparator, EnclosingEscaping);

            byte[] bytes = new byte[10000];
            int startIndex = 23;
            int index = startIndex;

            foreach (var entry in pipData.Entries)
            {
                var expectedIndex = index + PipDataEntry.BinarySize;
                entry.Write(bytes, ref index);

                // Ensure the correct number of bytes are written
                Assert.Equal(expectedIndex, index);
            }

            var endIndex = index;
            index = startIndex;

            List<PipDataEntry> readEntries = new List<PipDataEntry>();

            while (index < endIndex)
            {
                var expectedIndex = index + PipDataEntry.BinarySize;
                readEntries.Add(PipDataEntry.Read(bytes, ref index));

                // Ensure the correct number of bytes are read
                Assert.Equal(expectedIndex, index);
            }

            var readPipData = PipData.CreateInternal(pipData.HeaderEntry, PipDataEntryList.FromEntries(readEntries), StringId.Invalid);
            VerifyFullPipData(readPipData);
        }

        [Fact]
        public void BasicPipData()
        {
            var pipData = m_pipDataBuilder.ToPipData(EnclosingSeparator, EnclosingEscaping);
            VerifyFullPipData(pipData);
        }

        [Theory]
        [InlineData("C:\\", "dir with spaces")]
        [InlineData("a\\", "\"quoted\"\\")]
        [InlineData("", "")]
        [InlineData("a\t", "b\nc")]
        public void EscapeArgumentAfterConcatenation(string first, string second)
        {
            var raw = new PipDataBuilder(m_pathTable.StringTable);
            raw.Add(first);
            raw.Add(second);
            var arguments = new PipDataBuilder(m_pathTable.StringTable);
            arguments.AddEscapedArgument(raw.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping));
            var data = arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping);
            Assert.Equal(CommandLineEscaping.EscapeAsCommandLineWord(first + second), data.ToString(m_pathTable));
            Assert.Equal(AbstractParser.CommonEscapeArg(first + second),
                data.ToString(new PipFragmentRenderer(m_pathTable), useIpcEscaping: true));
            Assert.True(data.GetMaxPossibleLength(m_pathTable.StringTable) >= data.ToString(m_pathTable).Length);

            var bytes = new byte[data.Entries.Count * PipDataEntry.BinarySize];
            int index = 0;
            foreach (var entry in data.Entries) entry.Write(bytes, ref index);
            index = 0;
            var entries = new List<PipDataEntry>();
            while (index < bytes.Length) entries.Add(PipDataEntry.Read(bytes, ref index));
            var roundtrip = PipData.CreateInternal(data.HeaderEntry, PipDataEntryList.FromEntries(entries), StringId.Invalid);
            Assert.Equal<PipDataEntry>(data.Entries, roundtrip.Entries);
            Assert.Equal(data.ToString(m_pathTable), roundtrip.ToString(m_pathTable));
        }

        [Fact]
        public void EscapeArgumentDefersPathExpansion()
        {
            var raw = new PipDataBuilder(m_pathTable.StringTable);
            raw.Add("-I");
            raw.Add(m_uniqueEntry0);
            var arguments = new PipDataBuilder(m_pathTable.StringTable);
            arguments.AddEscapedArgument(raw.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping));
            var data = arguments.ToPipData(" ", PipDataFragmentEscaping.NoEscaping);
            Assert.Equal(CommandLineEscaping.EscapeAsCommandLineWord("-Iroot with spaces"),
                data.ToString(_ => "root with spaces", m_pathTable.StringTable));
            Assert.Equal("-Iother", data.ToString(_ => "other", m_pathTable.StringTable));
        }

        [Theory]
        [InlineData("C:\\", "dir with spaces")]
        [InlineData("a\\", "\"quoted\"\\")]
        [InlineData("", "")]
        [InlineData("a\t", "b\nc")]
        public void ConcatEscapesEachCompleteChild(string first, string second)
        {
            var raw = new PipDataBuilder(m_pathTable.StringTable);
            raw.Add(first);
            raw.Add(second);
            var nested = new PipDataBuilder(m_pathTable.StringTable);
            nested.Add(raw.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping));
            var arguments = new PipDataBuilder(m_pathTable.StringTable);
            arguments.Add(nested.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping));
            arguments.Add("");
            arguments.Add("tail with spaces");
            var data = arguments.ToPipData(" ", PipDataFragmentEscaping.EscapeEachArgument);
            string[] values = { first + second, "", "tail with spaces" };
            Assert.Equal(string.Join(" ", values.Select(value => CommandLineEscaping.EscapeAsCommandLineWord(value))), data.ToString(m_pathTable));
            Assert.Equal(string.Join(" ", values.Select(AbstractParser.CommonEscapeArg)),
                data.ToString(new PipFragmentRenderer(m_pathTable), useIpcEscaping: true));
            Assert.True(data.GetMaxPossibleLength(m_pathTable.StringTable) >= data.ToString(m_pathTable).Length);

            var bytes = new byte[(data.Entries.Count + 1) * PipDataEntry.BinarySize];
            int index = 0;
            data.HeaderEntry.Write(bytes, ref index);
            foreach (var entry in data.Entries) entry.Write(bytes, ref index);
            index = 0;
            var header = PipDataEntry.Read(bytes, ref index);
            var entries = new List<PipDataEntry>();
            while (index < bytes.Length) entries.Add(PipDataEntry.Read(bytes, ref index));
            var roundtrip = PipData.CreateInternal(header, PipDataEntryList.FromEntries(entries), StringId.Invalid);
            Assert.Equal(data.HeaderEntry, roundtrip.HeaderEntry);
            Assert.Equal<PipDataEntry>(data.Entries, roundtrip.Entries);
            Assert.Equal(data.ToString(m_pathTable), roundtrip.ToString(m_pathTable));
        }

        [Fact]
        public void ConcatArgumentEscapingDefersPathsAndPreservesChildRules()
        {
            var raw = new PipDataBuilder(m_pathTable.StringTable);
            raw.Add("-I");
            raw.Add(m_uniqueEntry0);
            var arguments = new PipDataBuilder(m_pathTable.StringTable);
            arguments.Add(raw.ToPipData(string.Empty, PipDataFragmentEscaping.NoEscaping));
            raw.Clear();
            raw.Add("already quoted");
            arguments.Add(raw.ToPipData(string.Empty, PipDataFragmentEscaping.CRuntimeArgumentRules));
            var data = arguments.ToPipData(" | ", PipDataFragmentEscaping.EscapeEachArgument);
            string quotedChild = CommandLineEscaping.EscapeAsCommandLineWord("already quoted");
            Assert.Equal(CommandLineEscaping.EscapeAsCommandLineWord("-Iroot with spaces") + " | " +
                CommandLineEscaping.EscapeAsCommandLineWord(quotedChild),
                data.ToString(_ => "root with spaces", m_pathTable.StringTable));
            Assert.Equal(AbstractParser.CommonEscapeArg("-I" + m_uniqueEntry0.ToString(m_pathTable)) + " | " +
                AbstractParser.CommonEscapeArg(quotedChild),
                data.ToString(new PipFragmentRenderer(m_pathTable), useIpcEscaping: true));
            raw.Clear();
            var empty = raw.ToPipData(" ", PipDataFragmentEscaping.EscapeEachArgument);
            Assert.Equal("", empty.ToString(m_pathTable));
            Assert.Equal(0, empty.GetMaxPossibleLength(m_pathTable.StringTable));
        }

        [Fact]
        public void OrdinaryNestedCRuntimeEscapingDoesNotInheritIpcEscaping()
        {
            var child = new PipDataBuilder(m_pathTable.StringTable);
            child.Add("a b");
            child.Add("a\\\"b");
            var nested = child.ToPipData(" ", PipDataFragmentEscaping.CRuntimeArgumentRules);
            var parent = new PipDataBuilder(m_pathTable.StringTable);
            parent.Add(nested);
            var data = parent.ToPipData(" ", PipDataFragmentEscaping.NoEscaping);
            Assert.Equal(nested.ToString(m_pathTable),
                data.ToString(new PipFragmentRenderer(m_pathTable), useIpcEscaping: true));
            Assert.Equal(data.ToString(m_pathTable).Length, data.GetMaxPossibleLength(m_pathTable.StringTable));
        }

        [Theory]
        [InlineData(PipDataFragmentEscaping.NoEscaping, -3)]
        [InlineData(PipDataFragmentEscaping.CRuntimeArgumentRules, -3)]
        [InlineData(PipDataFragmentEscaping.EscapeEachArgument, 0)]
        public void EmptyFragmentLengthPreservesLegacySeparatorAdjustment(PipDataFragmentEscaping escaping, int expected)
        {
            var builder = new PipDataBuilder(m_pathTable.StringTable);
            var data = builder.ToPipData(" | ", escaping);
            var method = typeof(PipData).GetMethod("GetMaxLength", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.Equal(expected, (int)method.Invoke(null, new object[] { data, new PipFragmentRenderer(m_pathTable) }));
        }

        [Fact]
        public void CopiedPipDataFragment()
        {
            var pipData = m_pipDataBuilder.ToPipData(EnclosingSeparator, EnclosingEscaping);

            // Create a pip fragment and verify that fragment
            var pipFragment = PipFragment.CreateNestedFragment(pipData);

            XAssert.IsTrue(pipFragment.FragmentType == PipFragmentType.NestedFragment);
            VerifyFullPipData(pipFragment.GetNestedFragmentValue());
        }

        [Fact]
        public void CreatePipDataWithCursors()
        {
            var pipDataStart = m_pipDataBuilder.ToPipData(EnclosingSeparator, EnclosingEscaping, m_cursorStart);
            var pipData0 = m_pipDataBuilder.ToPipData(EnclosingSeparator, EnclosingEscaping, m_cursor0);
            var pipData1 = m_pipDataBuilder.ToPipData(EnclosingSeparator, EnclosingEscaping, m_cursor1);
            var pipDataEnd = m_pipDataBuilder.ToPipData(EnclosingSeparator, EnclosingEscaping, m_cursorEnd);

            VerifyFullPipData(pipDataStart);

            XAssert.AreEqual(2, pipData0.FragmentCount);
            VerifyPipDataFromCursor0(pipData0.GetEnumerator());

            XAssert.AreEqual(1, pipData1.FragmentCount);
            VerifyPipDataFromCursor1(pipData1.GetEnumerator());

            XAssert.AreEqual(0, pipDataEnd.FragmentCount);
            VerifyPipDataFromCursorEnd(pipDataEnd.GetEnumerator());
        }

        private void VerifyFullPipData(PipData pipData)
        {
            var enumerator = pipData.GetEnumerator();
            XAssert.AreEqual(NumStandardBlockFragments + 2, pipData.FragmentCount);

            VerifyStandardBlock(ref enumerator);

            VerifyPipDataFromCursor0(enumerator);
        }

        private void VerifyPipDataFromCursor0(PipData.FragmentEnumerator enumerator)
        {
            PipFragment fragment;

            AssertMoveNext(ref enumerator, out fragment, PipFragmentType.NestedFragment);
            var nestedFragment0 = fragment;
            VerifyNested0(nestedFragment0);

            VerifyPipDataFromCursor1(enumerator);
        }

        private void VerifyPipDataFromCursor1(PipData.FragmentEnumerator enumerator)
        {
            PipFragment fragment;

            AssertMoveNext(ref enumerator, out fragment, PipFragmentType.NestedFragment);
            var nestedFragment1 = fragment;
            VerifyNested1(nestedFragment1);

            VerifyPipDataFromCursorEnd(enumerator);
        }

        private void VerifyPipDataFromCursorEnd(PipData.FragmentEnumerator enumerator)
        {
            XAssert.IsFalse(enumerator.MoveNext());
        }

        private void VerifyNested0(PipFragment fragment)
        {
            PipData nestedData = fragment.GetNestedFragmentValue();
            XAssert.AreEqual(NumStandardBlockFragments + 1, nestedData.FragmentCount);
            using (var nestedEnumerator = nestedData.GetEnumerator())
            {
                var localEnumerator = nestedEnumerator;

                AssertMoveNext(ref localEnumerator, out fragment, PipFragmentType.AbsolutePath);
                XAssert.AreEqual(m_uniqueEntry0, fragment.GetPathValue());

                VerifyStandardBlock(ref localEnumerator);

                XAssert.IsFalse(localEnumerator.MoveNext());
            }
        }

        private void VerifyNested1(PipFragment fragment)
        {
            PipData nestedData = fragment.GetNestedFragmentValue();
            XAssert.AreEqual(NumStandardBlockFragments + 1, nestedData.FragmentCount);
            using (var nestedEnumerator = nestedData.GetEnumerator())
            {
                var localEnumerator = nestedEnumerator;

                VerifyStandardBlock(ref localEnumerator);

                AssertMoveNext(ref localEnumerator, out fragment, PipFragmentType.StringLiteral);
                XAssert.AreEqual(UniqueEntry1, m_pathTable.StringTable.GetString(fragment.GetStringIdValue()));

                XAssert.IsFalse(localEnumerator.MoveNext());
            }
        }

        private AbsolutePath Path1 => AbsolutePath.Create(m_pathTable, Path.Combine(A(@"x", "absolute", "dir"), CaseVaryingString.ToUpper()));

        private AbsolutePath Path2 => AbsolutePath.Create(m_pathTable, Path.Combine(A("x", "absolutePath"), CaseVaryingString.ToUpper()));

        private FileArtifact SourceFile => FileArtifact.CreateSourceFile(Path1);

        private FileArtifact OutputFile => FileArtifact.CreateOutputFile(Path1);

        private FileArtifact RewrittenFile => OutputFile.CreateNextWrittenVersion();

        private FileArtifact RewrittenFile2 => RewrittenFile.CreateNextWrittenVersion();

        private int NumStandardBlockFragments => Descriptors.Count;

        // Add new fragments to verify to this list
        private List<(Action<PipDataBuilder> add, PipFragmentType expectedFragmentType, Action<PipFragment> verify, int line)> Descriptors =>
            new List<(Action<PipDataBuilder>, PipFragmentType, Action<PipFragment>, int)>
            {
                CreateDescriptor(Path1, PipFragmentType.AbsolutePath, f => f.GetPathValue(), (v, p) => p.Add(v)),
                CreateDescriptor(Path2, PipFragmentType.AbsolutePath, f => f.GetPathValue(), (v, p) => p.Add(v)),
                CreateDescriptor(CaseVaryingString, PipFragmentType.StringLiteral, f => m_pathTable.StringTable.GetString(f.GetStringIdValue()), (v, p) => p.Add(v)),
                CreateDescriptor(m_expectedStringId0, PipFragmentType.StringLiteral, f => f.GetStringIdValue(), (v, p) => p.Add(v)),
                CreateDescriptor(SourceFile, PipFragmentType.VsoHash, f => f.GetFileValue(), (v, p) => p.AddVsoHash(v)),
                CreateDescriptor(OutputFile, PipFragmentType.VsoHash, f => f.GetFileValue(), (v, p) => p.AddVsoHash(v)),
                CreateDescriptor(RewrittenFile, PipFragmentType.VsoHash, f => f.GetFileValue(), (v, p) => p.AddVsoHash(v)),
                CreateDescriptor(RewrittenFile2, PipFragmentType.VsoHash, f => f.GetFileValue(), (v, p) => p.AddVsoHash(v)),
                CreateDescriptor(new DirectoryArtifact(Path1, 1, isSharedOpaque: true), PipFragmentType.DirectoryId, f => f.GetDirectoryValue(), (v, p) => p.AddDirectoryId(v)),
            };

        private (Action<PipDataBuilder> add, PipFragmentType expectedFragmentType, Action<PipFragment> verify, int line) CreateDescriptor<T>(
            T value,
            PipFragmentType expectedFragmentType,
            Func<PipFragment, T> getFragmentValue,
            Action<T, PipDataBuilder> add,
            [CallerLineNumber] int line = 0)
        {
            return
            (
                b => add(value, b),
                expectedFragmentType,
               f => XAssert.AreEqual(value, getFragmentValue(f), $"Defined at line {line}."),
               line
            );
        }

        private void AddStandardBlock(PipDataBuilder pipDataBuilder)
        {
            foreach (var descriptor in Descriptors)
            {
                descriptor.add(pipDataBuilder);
            }
        }

        private void VerifyStandardBlock(ref PipData.FragmentEnumerator enumerator)
        {
            foreach (var descriptor in Descriptors)
            {
                PipFragment fragment;
                AssertMoveNext(ref enumerator, out fragment, descriptor.expectedFragmentType, descriptor.line);
                descriptor.verify(fragment);
            }
        }

        private static void AssertMoveNext(ref PipData.FragmentEnumerator enumerator, out PipFragment fragment, PipFragmentType? expectedFragmentType = null, [CallerLineNumber] int line = 0)
        {
            var message = $"Defined at line {line}.";
            XAssert.IsTrue(enumerator.MoveNext(), message);
            fragment = enumerator.Current;
            XAssert.IsTrue(fragment.IsValid, message);
            if (expectedFragmentType.HasValue)
            {
                XAssert.AreEqual(expectedFragmentType.Value, fragment.FragmentType, message);
            }
        }
    }
}

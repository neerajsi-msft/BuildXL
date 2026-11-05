// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.Utilities.Core;
using Test.BuildXL.TestUtilities.Xunit;
using Xunit;
using BuildXL.Scheduler.Fingerprints;
using BuildXL.Utilities.Collections;
using BuildXL.Pips.Operations;

namespace Test.BuildXL.Scheduler
{
    public class SourceSealWithPatternsTests : XunitBuildXLTest
    {
        private readonly ITestOutputHelper m_output;
        public SourceSealWithPatternsTests(ITestOutputHelper output)
            : base(output) { m_output = output; }

        [Fact]
        public void TestSourceSealPatterns()
        {
            var pt = new PathTable();
            var st = pt.StringTable;
            var dir1 = AbsolutePath.Create(pt, X("/c/sourceseal"));

            var f1 = dir1.Combine(pt, "file1");
            var f2 = dir1.Combine(pt, "file2");
            var f3 = dir1.Combine(pt, "file3.txt");
            var f4 = dir1.Combine(pt, "file4.txt");
            var f5 = dir1.Combine(pt, "file5.txt");
            var f6 = dir1.Combine(pt, "a");

            var nestedDir = AbsolutePath.Create(pt, X("/c/sourceseal/nested"));
            var nestedf1 = nestedDir.Combine(pt, "nested1");
            var nestedf2 = nestedDir.Combine(pt, "nested2.cs");

            var pattern1 = StringId.Create(st, "*");
            var pattern2 = StringId.Create(st, "*.txt");
            var pattern3 = StringId.Create(st, "*.cs");
            var pattern4 = StringId.Create(st, "file5.txt");
            var pattern5 = StringId.Create(st, "file1*");

            SourceSealWithPatterns sourceSeal1 = new SourceSealWithPatterns(dir1, ReadOnlyArray<StringId>.From(new[] { pattern1, pattern2 }), true);

            // Wildcard matches everything
            AssertTrue(sourceSeal1.Contains(pt, f1));
            AssertTrue(sourceSeal1.Contains(pt, f2));
            AssertTrue(sourceSeal1.Contains(pt, f3));
            AssertTrue(sourceSeal1.Contains(pt, f4));
            AssertTrue(sourceSeal1.Contains(pt, f5));
            AssertTrue(sourceSeal1.Contains(pt, f6));

            AssertTrue(!sourceSeal1.Contains(pt, nestedf1));
            AssertTrue(!sourceSeal1.Contains(pt, nestedf2));
            AssertTrue(sourceSeal1.Contains(pt, nestedf1, isTopDirectoryOnlyOverride: false));
            AssertTrue(sourceSeal1.Contains(pt, nestedf2, isTopDirectoryOnlyOverride: false));

            SourceSealWithPatterns sourceSeal2 = new SourceSealWithPatterns(dir1, ReadOnlyArray<StringId>.From(new[] { pattern3, pattern4, pattern5 }), true);
            // *.cs, file5.txt, file1*
            AssertTrue(sourceSeal2.Contains(pt, f1));
            AssertTrue(!sourceSeal2.Contains(pt, f2));
            AssertTrue(!sourceSeal2.Contains(pt, f3));
            AssertTrue(!sourceSeal2.Contains(pt, f4));
            AssertTrue(sourceSeal2.Contains(pt, f5));
            AssertTrue(!sourceSeal2.Contains(pt, f6));
            AssertTrue(!sourceSeal2.Contains(pt, nestedf1));
            AssertTrue(!sourceSeal2.Contains(pt, nestedf2));
            AssertTrue(!sourceSeal2.Contains(pt, nestedf1, isTopDirectoryOnlyOverride: false));
            AssertTrue(sourceSeal2.Contains(pt, nestedf2, isTopDirectoryOnlyOverride: false));

        }

        [Fact]
        public void ExclusionsAreComponentAwareAndDoNotAuthorizeSubtreeProbes()
        {
            var pt = new PathTable();
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var exclusions = new SourceSealExclusions(
                ReadOnlyArray<RelativePath>.From(new[] {
                    RelativePath.Create(pt.StringTable, "private/state"),
                    RelativePath.Create(pt.StringTable, ".git"),
                    RelativePath.Create(pt.StringTable, "subrepo/.git"),
                }));
            var seal = new SourceSealWithPatterns(root, ReadOnlyArray<StringId>.Empty, false, exclusions);

            foreach (var path in new[] { ".git", "subrepo/.git", "private/state" })
            {
                var excluded = root.Combine(pt, RelativePath.Create(pt.StringTable, path));
                Assert.False(seal.Contains(pt, excluded));
                Assert.True(seal.Contains(pt, excluded, allowExactExcludedProbe: true));
                var child = excluded.Combine(pt, "config");
                Assert.False(seal.Contains(pt, child));
                Assert.False(seal.Contains(pt, child, allowExactExcludedProbe: true));
            }

            foreach (var path in new[] { "public/file", ".github", "nested/.git", "nested/.git/config", "nested/.gitignore", "private/stateful", "other/private/state" })
            {
                Assert.True(seal.Contains(pt, root.Combine(pt, RelativePath.Create(pt.StringTable, path))));
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ExclusionsRoundTrip(bool empty)
        {
            var pt = new PathTable();
            var exclusions = new SourceSealExclusions(
                empty ? ReadOnlyArray<RelativePath>.Empty
                    : ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, "private/state") }));
            using (var stream = new System.IO.MemoryStream())
            {
                using (var writer = new BuildXLWriter(false, stream, true, false))
                {
                    exclusions.Serialize(writer);
                }

                stream.Position = 0;
                using (var reader = new BuildXLReader(false, stream, false))
                {
                    var restored = SourceSealExclusions.Deserialize(reader);
                    Assert.Equal(exclusions.Paths.ToArray(), restored.Paths.ToArray());
                    Assert.Equal(empty, restored.IsEmpty);
                }
            }
        }

        [Fact]
        public void SingleWildcardPatternMatchTests()
        {
            string filename = "file.txt";
            string[] passingPatterns = new string[] { "*", "fi*", "*t", "*.txt", "*file.txt", "file*.txt", "file*", "file.tx*" };
            foreach (var pattern in passingPatterns)
            {
                AssertTrue(SourceSealWithPatterns.SingleWildcardPatternMatch(filename, pattern), $"File name, '{filename}' does not pass the pattern, '{pattern}'");
            }

            string[] notPassingPatterns = new string[] { "*a", "i*", "f*axt", "file1.txt", "file", "file.tx", "abcdefgyhu"};
            foreach (var pattern in notPassingPatterns)
            {
                AssertTrue(!SourceSealWithPatterns.SingleWildcardPatternMatch(filename, pattern), $"File name, '{filename}' passes the pattern, '{pattern}'");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void IndexUsesDeepestRootAndTerminalLiteralExclusions(bool reverse)
        {
            var pt = new PathTable();
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var nested = root.Combine(pt, "nested");
            var broad = new SourceSealWithPatterns(root, ReadOnlyArray<StringId>.Empty, false,
                new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] {
                    RelativePath.Create(pt.StringTable, ".git"),
                    RelativePath.Create(pt.StringTable, "nested/private"),
                })));
            var narrow = new SourceSealWithPatterns(nested,
                ReadOnlyArray<StringId>.From(new[] { StringId.Create(pt.StringTable, "*.cs") }), true);
            var index = new SourceSealDirectoryIndex(pt, reverse ? new[] { narrow, broad } : new[] { broad, narrow });

            Assert.True(index.IsValid);
            foreach (var path in new[] { ".git/config", "nested/private", "nested/private/file.cs" })
            {
                Assert.Equal(SourceSealMatch.Excluded, index.Match(pt, root.Combine(pt, RelativePath.Create(pt.StringTable, path))));
            }

            Assert.Equal(SourceSealMatch.Included, index.Match(pt, nested.Combine(pt, "file.cs")));
            Assert.Equal(SourceSealMatch.None, index.Match(pt, nested.Combine(pt, "file.h")));
            Assert.Equal(SourceSealMatch.None, index.Match(pt, nested.Combine(pt, RelativePath.Create(pt.StringTable, "deep/file.cs"))));
            Assert.True(index.IsUnderTopDirectoryOnly(pt, nested.Combine(pt, RelativePath.Create(pt.StringTable, "deep/file.cs"))));
            Assert.Equal(SourceSealMatch.Root, index.Match(pt, root));
            Assert.Equal(SourceSealMatch.Root, index.Match(pt, nested));
            Assert.Equal(SourceSealMatch.Included, index.Match(pt, root.Combine(pt, ".github")));
            Assert.Equal(SourceSealMatch.Included, index.Match(pt, root.Combine(pt, ".git"), allowExactExcludedProbe: true));
            Assert.Equal(SourceSealMatch.Excluded, index.Match(pt, root.Combine(pt, RelativePath.Create(pt.StringTable, ".git/config")), allowExactExcludedProbe: true));
        }

        [Theory]
        [InlineData("private")]
        [InlineData("private/nested")]
        public void IndexRejectsRootsAtOrBelowExclusions(string relativeRoot)
        {
            var pt = new PathTable();
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var excluded = root.Combine(pt, "private");
            var positive = root.Combine(pt, RelativePath.Create(pt.StringTable, relativeRoot));
            var seals = new[] {
                new SourceSealWithPatterns(root, ReadOnlyArray<StringId>.Empty, false,
                    new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, "private") }))),
                new SourceSealWithPatterns(positive, ReadOnlyArray<StringId>.Empty, false),
            };

            foreach (var ordered in new[] { seals, new[] { seals[1], seals[0] } })
            {
                var index = new SourceSealDirectoryIndex(pt, ordered);
                Assert.False(index.IsValid);
                Assert.Equal(positive, index.ConflictingRoot);
                Assert.Equal(excluded, index.ConflictingExclusion);
            }
        }

        [Fact]
        public void PolicyIndexesAreSharedByMeaningNotArtifactIdentityOrOrder()
        {
            var pt = new PathTable();
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var pattern = ReadOnlyArray<StringId>.From(new[] { StringId.Create(pt.StringTable, "*.cs") });
            var first = new SourceSealWithPatterns(root, pattern, false,
                new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, ".git") })));
            var equivalent = new SourceSealWithPatterns(root, pattern, false,
                new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, ".git") })));
            var second = new SourceSealWithPatterns(root.Combine(pt, "nested"), ReadOnlyArray<StringId>.Empty, true);
            var cache = new SourceSealDirectoryIndex.Cache();
            var index = cache.GetOrAdd(pt, new[] { first, second });

            System.Threading.Tasks.Parallel.For(0, 64, _ =>
                Assert.Same(index, cache.GetOrAdd(pt, new[] { second, equivalent, first })));
            Assert.NotSame(index, cache.GetOrAdd(pt, new[] { first }));
            Assert.NotSame(index, cache.GetOrAdd(pt, new[] { new SourceSealWithPatterns(root, pattern, true, first.Exclusions), second }));
            Assert.NotSame(index, cache.GetOrAdd(pt, new[] { new SourceSealWithPatterns(root, ReadOnlyArray<StringId>.Empty, false, first.Exclusions), second }));
            Assert.NotSame(index, cache.GetOrAdd(pt, new[] { new SourceSealWithPatterns(root, pattern, false,
                new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, ".jj") }))), second }));

            var pipId = new global::BuildXL.Pips.PipId(1);
            cache.Bind(pipId, index);
            Assert.Same(index, cache.GetOrAdd(pt, ThrowIfEnumerated(), pipId));
        }

        [Fact]
        public void DefaultPolicyHasValueEquality()
        {
            Assert.Equal(default(SourceSealWithPatterns), default(SourceSealWithPatterns));
            Assert.Equal(0, default(SourceSealWithPatterns).GetHashCode());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void LookupCachePreservesBoundariesPatternsAndProbeFlags(bool reverse)
        {
            var pt = new PathTable();
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var nested = root.Combine(pt, "nested");
            var index = new SourceSealDirectoryIndex(pt, new[] {
                new SourceSealWithPatterns(root, ReadOnlyArray<StringId>.Empty, false,
                    new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] {
                        RelativePath.Create(pt.StringTable, ".git"),
                        RelativePath.Create(pt.StringTable, ".git/state"),
                        RelativePath.Create(pt.StringTable, "nested/private"),
                    }))),
                new SourceSealWithPatterns(nested,
                    ReadOnlyArray<StringId>.From(new[] { StringId.Create(pt.StringTable, "*.cs") }), true),
                new SourceSealWithPatterns(nested,
                    ReadOnlyArray<StringId>.From(new[] { StringId.Create(pt.StringTable, "*.txt") }), false),
            });
            var paths = new[] {
                "nested/file.cs", "nested/file.h", "nested/file.txt", "nested/private",
                "nested/private/file.cs", "nested/deep/file.cs", "nested/deep/file.txt",
                "nested/.git/config", ".git", ".git/state", ".git/config",
                ".github", "public/file", "public/deep/file", "nested", "",
            };
            if (reverse)
            {
                System.Array.Reverse(paths);
            }

            var cache = default(SourceSealDirectoryIndex.LookupCache);
            foreach (var relative in paths)
            {
                var path = relative.Length == 0 ? root : root.Combine(pt, RelativePath.Create(pt.StringTable, relative));
                foreach (bool probe in new[] { false, true, false })
                {
                    Assert.Equal(index.Match(pt, path, probe), index.Match(pt, path, ref cache, probe));
                }
            }

            var outside = AbsolutePath.Create(pt, X("/c/elsewhere/file.cs"));
            Assert.Equal(SourceSealMatch.None, index.Match(pt, outside, ref cache));
            Assert.Equal(SourceSealMatch.None, index.Match(pt, AbsolutePath.Invalid, ref cache));
            Assert.True(cache.Hits > 0);
            Assert.True(cache.Misses > 0);
        }

        [Fact]
        public void LookupCacheAvoidsRepeatedDirectoryResolutionButChecksBoundaryLeaves()
        {
            var pt = new PathTable();
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var index = new SourceSealDirectoryIndex(pt, new[] {
                new SourceSealWithPatterns(root, ReadOnlyArray<StringId>.Empty, false,
                    new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, ".git") }))),
            });
            var cache = default(SourceSealDirectoryIndex.LookupCache);
            for (int i = 0; i < 100; i++)
            {
                Assert.Equal(SourceSealMatch.Included, index.Match(pt, root.Combine(pt, "file" + i), ref cache));
            }

            var excluded = root.Combine(pt, ".git");
            Assert.Equal(SourceSealMatch.Excluded, index.Match(pt, excluded, ref cache));
            Assert.Equal(SourceSealMatch.Included, index.Match(pt, excluded, ref cache, allowExactExcludedProbe: true));
            Assert.Equal(SourceSealMatch.Excluded, index.Match(pt, excluded, ref cache));
            Assert.Equal(102, cache.Hits);
            Assert.Equal(1, cache.Misses);

            Assert.Equal(SourceSealMatch.Excluded, index.Match(pt, excluded.Combine(pt, "config"), ref cache, allowExactExcludedProbe: true));
            Assert.Equal(2, cache.Misses);
        }

        [Fact]
        public void LookupCacheReusesBoundaryFreeSubtreesAndDoesNotLeakBetweenIndexes()
        {
            var pt = new PathTable();
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var included = new SourceSealDirectoryIndex(pt, new[] { new SourceSealWithPatterns(root, ReadOnlyArray<StringId>.Empty, false) });
            var excluded = new SourceSealDirectoryIndex(pt, new[] {
                new SourceSealWithPatterns(root, ReadOnlyArray<StringId>.Empty, false,
                    new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, "deep") }))),
            });
            System.Threading.Tasks.Parallel.For(0, 32, _ =>
            {
                var cache = default(SourceSealDirectoryIndex.LookupCache);
                foreach (var relative in new[] { "file", "deep/file", "deep/child/file" })
                {
                    var path = root.Combine(pt, RelativePath.Create(pt.StringTable, relative));
                    Assert.Equal(SourceSealMatch.Included, included.Match(pt, path, ref cache));
                }

                Assert.Equal(2, cache.Hits);
                Assert.Equal(1, cache.Misses);
                var child = root.Combine(pt, RelativePath.Create(pt.StringTable, "deep/child/file"));
                Assert.Equal(SourceSealMatch.Excluded, excluded.Match(pt, child, ref cache));
                Assert.Equal(SourceSealMatch.Included, included.Match(pt, child, ref cache));
                Assert.Equal(3, cache.Misses);
            });
        }

        [Fact]
        public void EmptyPoliciesShareOneIndexWithoutPerPipEntriesIncludingAfterReload()
        {
            var pt = new PathTable();
            var cache = new SourceSealDirectoryIndex.Cache();
            for (uint id = 1; id <= 256; id++)
            {
                var pip = new global::BuildXL.Pips.PipId(id);
                var index = cache.GetOrAdd(pt, System.Array.Empty<SourceSealWithPatterns>(), pip);
                Assert.Same(SourceSealDirectoryIndex.Empty, index);
                cache.Bind(pip, index);
                Assert.False(cache.TryGet(pip, out _));
            }
            Assert.Equal(0, cache.BoundIndexCount);
            var reloaded = new SourceSealDirectoryIndex.Cache();
            Assert.Same(SourceSealDirectoryIndex.Empty, reloaded.GetOrAdd(pt, System.Array.Empty<SourceSealWithPatterns>(), new global::BuildXL.Pips.PipId(123)));
            Assert.Equal(SourceSealMatch.None, SourceSealDirectoryIndex.Empty.Match(pt, AbsolutePath.Create(pt, X("/c/source/file"))));
        }

        [Fact]
        public void WarmPolicySetLookupsDoNotAllocateInProportionToTheSealCount()
        {
            var pt = new PathTable();
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var policies = new SourceSealWithPatterns[256];
            for (int i = 0; i < policies.Length; i++)
            {
                policies[i] = new SourceSealWithPatterns(root.Combine(pt, "root" + i),
                    ReadOnlyArray<StringId>.From(new[] { StringId.Create(pt.StringTable, "*.h") }), false,
                    new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, ".git") })));
            }
            var cache = new SourceSealDirectoryIndex.Cache();
            var expected = cache.GetOrAdd(pt, policies);
            for (int i = 0; i < 100; i++) cache.GetOrAdd(pt, policies);
            const int iterations = 1000;
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            SourceSealDirectoryIndex actual = null;
            for (int i = 0; i < iterations; i++) actual = cache.GetOrAdd(pt, policies);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
            watch.Stop();
            Assert.Same(expected, actual);
            Assert.True(allocated < iterations * 512L, $"Warm hits allocated {allocated} bytes.");
            m_output.WriteLine($"256-seal warm hits: {allocated / iterations} bytes/call, {watch.ElapsedMilliseconds}ms/{iterations} calls");

            before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < iterations; i++)
            {
                System.GC.KeepAlive(new System.Collections.Generic.HashSet<SourceSealWithPatterns>(policies));
            }
            long formerKeyBytes = System.GC.GetAllocatedBytesForCurrentThread() - before;
            m_output.WriteLine($"Former per-call HashSet key construction alone: {formerKeyBytes / iterations} bytes/call");
        }

        private static System.Collections.Generic.IEnumerable<SourceSealWithPatterns> ThrowIfEnumerated()
        {
            Assert.Fail("A bound pip must reuse its validated index.");
            yield break;
        }

        [Fact]
        public void ConstructionStylePolicyLookupMeasurements()
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pt = context.PathTable;
            using var table = new global::BuildXL.Pips.PipTable(pt, context.SymbolTable,
                initialBufferSize: 1024, maxDegreeOfParallelism: 1, debug: false);
            var ids = new global::BuildXL.Pips.PipId[256];
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var patterns = ReadOnlyArray<StringId>.From(new[] { StringId.Create(pt.StringTable, "*.h") });
            var exclusions = new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, ".git") }));
            for (int i = 0; i < ids.Length; i++)
            {
                var directory = root.Combine(pt, "root" + i);
                var seal = new SealDirectory(directory,
                    CollectionUtilities.EmptySortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer>(OrdinalFileArtifactComparer.Instance),
                    CollectionUtilities.EmptySortedReadOnlyArray<DirectoryArtifact, OrdinalDirectoryArtifactComparer>(OrdinalDirectoryArtifactComparer.Instance),
                    SealDirectoryKind.SourceAllDirectories, PipProvenance.CreateDummy(context),
                    ReadOnlyArray<StringId>.Empty, patterns, sourceExclusions: exclusions);
                seal.SetDirectoryArtifact(new DirectoryArtifact(directory, (uint)i + 1));
                ids[i] = table.Add((uint)i + 1, seal);
            }

            System.Collections.Generic.IEnumerable<SourceSealWithPatterns> ReadPolicies(bool memoized)
            {
                foreach (var id in ids)
                {
                    if (memoized)
                    {
                        if (!table.SourceSealDirectoryIndices.TryGetSealPolicy(table, id, out var policy))
                        {
                            throw new System.InvalidOperationException("Expected a source seal policy.");
                        }
                        yield return policy;
                    }
                    else
                    {
                        var kind = table.GetSealDirectoryKind(id);
                        yield return new SourceSealWithPatterns(table.GetSealDirectoryRoot(id),
                            table.GetSourceSealDirectoryPatterns(id), kind == SealDirectoryKind.SourceTopDirectoryOnly,
                            table.GetSourceSealDirectoryExclusions(id));
                    }
                }
            }

            var expected = table.SourceSealDirectoryIndices.GetOrAdd(pt, ReadPolicies(memoized: false));
            void Measure(bool memoized)
            {
                for (int i = 0; i < 100; i++) table.SourceSealDirectoryIndices.GetOrAdd(pt, ReadPolicies(memoized));
                const int iterations = 1000;
                long before = System.GC.GetAllocatedBytesForCurrentThread();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                SourceSealDirectoryIndex actual = null;
                for (int i = 0; i < iterations; i++) actual = table.SourceSealDirectoryIndices.GetOrAdd(pt, ReadPolicies(memoized));
                watch.Stop();
                long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.Same(expected, actual);
                m_output.WriteLine($"256-seal construction-style hits (memoized={memoized}): {allocated / iterations} bytes/call, {watch.ElapsedMilliseconds}ms/{iterations} calls");
            }
            Measure(memoized: false);
            Measure(memoized: true);
            m_output.WriteLine($"SourceSealWithPatterns size: {System.Runtime.CompilerServices.Unsafe.SizeOf<SourceSealWithPatterns>()} bytes");
        }

        [Theory]
        [InlineData(SealDirectoryKind.SourceAllDirectories)]
        [InlineData(SealDirectoryKind.SourceTopDirectoryOnly)]
        public void SealPolicyMemoPreservesMetadataDuringConcurrentLookup(SealDirectoryKind kind)
        {
            var context = BuildXLContext.CreateInstanceForTesting();
            var pt = context.PathTable;
            using var table = new global::BuildXL.Pips.PipTable(pt, context.SymbolTable,
                initialBufferSize: 1024, maxDegreeOfParallelism: 1, debug: false);
            var root = AbsolutePath.Create(pt, X("/c/source"));
            var patterns = ReadOnlyArray<StringId>.From(new[] { StringId.Create(pt.StringTable, "*.h") });
            var exclusions = new SourceSealExclusions(ReadOnlyArray<RelativePath>.From(new[] { RelativePath.Create(pt.StringTable, ".git") }));
            var seal = new SealDirectory(root,
                CollectionUtilities.EmptySortedReadOnlyArray<FileArtifact, OrdinalFileArtifactComparer>(OrdinalFileArtifactComparer.Instance),
                CollectionUtilities.EmptySortedReadOnlyArray<DirectoryArtifact, OrdinalDirectoryArtifactComparer>(OrdinalDirectoryArtifactComparer.Instance),
                kind, PipProvenance.CreateDummy(context), ReadOnlyArray<StringId>.Empty, patterns, sourceExclusions: exclusions);
            seal.SetDirectoryArtifact(new DirectoryArtifact(root, 1));
            var id = table.Add(1, seal);
            var expected = new SourceSealWithPatterns(root, patterns, kind == SealDirectoryKind.SourceTopDirectoryOnly, exclusions);
            System.Threading.Tasks.Parallel.For(0, 64, _ =>
            {
                Assert.True(table.SourceSealDirectoryIndices.TryGetSealPolicy(table, id, out var policy));
                Assert.Equal(expected, policy);
                Assert.Equal(expected.GetHashCode(), policy.GetHashCode());
            });
            var nonSeal = table.Add(2, new HashSourceFile(FileArtifact.CreateSourceFile(root.Combine(pt, "file"))));
            Assert.False(table.SourceSealDirectoryIndices.TryGetSealPolicy(table, nonSeal, out var missing));
            Assert.Equal(default(SourceSealWithPatterns), missing);
        }

        [Fact]
        public void SingleWildcardPatternMatchTestsWithEmptyString()
        {
            string filename = "";
            string[] passingPatterns = new string[] { "*"};
            foreach (var pattern in passingPatterns)
            {
                AssertTrue(SourceSealWithPatterns.SingleWildcardPatternMatch(filename, pattern), $"File name, '{filename}' does not pass the pattern, '{pattern}'");
            }

            string[] notPassingPatterns = new string[] { "*a", "i*", "f*axt", "file1.txt", "file", "file.tx", "abcdefgyhu" };
            foreach (var pattern in notPassingPatterns)
            {
                AssertTrue(!SourceSealWithPatterns.SingleWildcardPatternMatch(filename, pattern), $"File name, '{filename}' passes the pattern, '{pattern}'");
            }
        }
    }
}

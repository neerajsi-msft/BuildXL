// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Linq;
using BuildXL.Pips.Graph;
using BuildXL.Utilities.Core;
using Test.BuildXL.TestUtilities.Xunit;

namespace Test.BuildXL.Pips
{
    public sealed class UnixDefaultsTests
    {
        [FactIfSupported(requiresUnixBasedOperatingSystem: true)]
        public void SystemLibraryAliasesAreOsDependencies()
        {
            var pathTable = new PathTable();
            var defaults = new PipGraph.UnixDefaults(pathTable, pipGraph: null);
            var directories = defaults.UntrackedDirectories.Select(directory => directory.Path);

            foreach (var path in new[] { "/lib", "/lib32", "/lib64", "/usr/lib", "/usr/lib32", "/usr/lib64" })
            {
                XAssert.IsTrue(directories.Contains(AbsolutePath.Create(pathTable, path)), path);
            }

            XAssert.IsFalse(directories.Contains(AbsolutePath.Create(pathTable, "/mnt/c")));
        }
    }
}

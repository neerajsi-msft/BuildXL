// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using Test.BuildXL.FrontEnd.Core;

namespace Test.BuildXL.FrontEnd.DirectGraph
{
    internal static class DirectGraphFixtureFiles
    {
        internal static string NormalizeSourceLiteral(string text) =>
            // Raw C# literals inherit source endings. Explicit LF-header payload tests must retain their literal CR.
            text.StartsWith("directgraph 3\r\n", StringComparison.Ordinal) ? text.Replace("\r\n", "\n") : text;

        internal static SpecEvaluationBuilder AddDirectGraphFile(this SpecEvaluationBuilder builder, string name, string text) =>
            builder.AddFile(name, NormalizeSourceLiteral(text));
    }
}

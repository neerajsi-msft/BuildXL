// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BuildXL.FrontEnd.Script.Ambients.Transformers;
using BuildXL.FrontEnd.Script.Values;
using System.Linq;
using BuildXL.Pips.Operations;
using BuildXL.Utilities.Core;
using Test.BuildXL.FrontEnd.Core;
using Xunit;
using static Test.BuildXL.TestUtilities.TestEnv;

namespace Test.DScript.Ast.Interpretation.Transformers
{
    [Trait("Category", "Transformers")]
    public sealed class SealSourceDirectoryOptionTests : DsTest
    {
        private readonly TestPipGraph m_pipGraph = new TestPipGraph();

        protected override TestPipGraph GetPipGraph() => m_pipGraph;

        public SealSourceDirectoryOptionTests(ITestOutputHelper output)
            : base(output)
        {
        }

        [Fact]
        public void ExclusionsAreAvailableFromDScript()
        {
            EvaluateExpressionWithNoErrors(@"
import {Transformer} from 'Sdk.Transformers';
export const result = Transformer.sealSourceDirectory({
    root: d`.`,
    include: 'allDirectories',
    excludedPaths: [r`private/state`],
    excludedNames: [a`.git`],
});", "result");

            var seal = m_pipGraph.RetrieveScheduledPips().OfType<SealDirectory>().Single();
            Assert.Equal(SealDirectoryKind.SourceAllDirectories, seal.Kind);
            Assert.Equal(RelativePath.Create(StringTable, "private/state"), seal.SourceExclusions.Paths[0]);
            Assert.Equal(PathAtom.Create(StringTable, ".git"), seal.SourceExclusions.Names[0]);
        }

        [Theory]
        [InlineData("excludedPaths: [p`absolute`]")]
        [InlineData("excludedPaths: [r``]")]
        [InlineData("excludedNames: [r`nested/name`]")]
        public void InvalidExclusionsAreRejected(string property)
        {
            Build().AddSpec(@"
import {Transformer} from 'Sdk.Transformers';
export const result = Transformer.sealSourceDirectory({ root: d`.`, " + property + @" });
").EvaluateWithFirstError();
        }

        [Fact]
        public void PropagateNestedNamespaceAndEnumWhenDoingImport()
        {
            const string Spec = @"
import {Transformer} from 'Sdk.Transformers';

const all = Transformer.SealSourceDirectoryOption.allDirectories;
const top = Transformer.SealSourceDirectoryOption.topDirectoryOnly;";

            var result = Build()
                .AddSpec("spec.dsc", Spec)
                .RootSpec("spec.dsc")
                .EvaluateExpressionsWithNoErrors("all", "top");

            Assert.Equal(SealSourceDirectoryOption.AllDirectories, (SealSourceDirectoryOption)((EnumValue) result["all"]).Value);
            Assert.Equal(SealSourceDirectoryOption.TopDirectoryOnly, (SealSourceDirectoryOption)((EnumValue) result["top"]).Value);
        }
    }
}

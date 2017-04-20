
using Orleans;
using Orleans.Runtime;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable ConvertToConstant.Local

namespace DefaultCluster.Tests.CodeGeneration
{
    public class CodeGeneratorTests_RequiringSilo : HostedTestClusterEnsureDefaultStarted
    {
        private readonly ITestOutputHelper output;

        public CodeGeneratorTests_RequiringSilo(ITestOutputHelper output, DefaultClusterFixture fixture) : base(fixture)
        {
            this.output = output;
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("UniqueKey")]
        public void CodeGen_GrainId_TypeCode()
        {
            var g1Key = GetRandomGrainId();
            ITestGrain g1 = this.GrainFactory.GetGrain<ITestGrain>(g1Key);
            GrainId id1 = ((GrainReference)g1).GrainId;
            UniqueKey k1 = id1.Key;
            Assert.True(id1.IsGrain, "GrainReference should be for a grain");
            Assert.Equal(UniqueKey.Category.Grain, k1.IdCategory);  // "GrainId should be for self-managed type"
            Assert.Equal(g1Key, k1.PrimaryKeyToLong());  // "Encoded primary key should match"
            Assert.Equal(1146670029, k1.BaseTypeCode);  // "Encoded type code data should match"
        }

        [Fact, TestCategory("BVT"), TestCategory("Functional"), TestCategory("CodeGen"), TestCategory("UniqueKey"), TestCategory("ActivationCollector")]
        public void CollectionTest_GrainId_TypeCode()
        {
            var g1Key = GetRandomGrainId();
            ICollectionTestGrain g1 = this.GrainFactory.GetGrain<ICollectionTestGrain>(g1Key);
            GrainId id1 = ((GrainReference)g1).GrainId;
            UniqueKey k1 = id1.Key;
            output.WriteLine("GrainId={0} UniqueKey={1} PK={2} KeyType={3} IdCategory={4}",
                id1, k1, id1.GetPrimaryKeyLong(), k1.IdCategory, k1.BaseTypeCode);
            Assert.True(id1.IsGrain, "GrainReference should be for a grain");
            Assert.Equal(UniqueKey.Category.Grain, k1.IdCategory);  // "GrainId should be for self-managed type"
            Assert.Equal(g1Key, k1.PrimaryKeyToLong());  // "Encoded primary key should match"
            Assert.Equal(1381240679, k1.BaseTypeCode);  // "Encoded type code data should match"
        }
    }

}

// ReSharper restore ConvertToConstant.Local

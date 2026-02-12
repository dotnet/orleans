using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for Orleans' ability to handle types defined outside of the grain interface and implementation assemblies.
    /// Verifies that the serialization system correctly handles external types (like enums) passed as parameters to grain methods.
    /// </summary>
    public class ExternalTypesTests : HostedTestClusterEnsureDefaultStarted
    {
        public ExternalTypesTests(DefaultClusterFixture fixture) : base(fixture)
        {
        }

        /// <summary>
        /// Tests that grains can successfully receive and process enum types defined in external assemblies.
        /// This validates Orleans' type system's ability to serialize/deserialize types not directly referenced
        /// in the grain interface assembly, which is important for supporting shared domain models.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Serialization")]
        public async Task ExternalTypesTest_GrainWithEnumExternalTypeParam()
        {
            var grainWithEnumTypeParam = this.GrainFactory.GetGrain<IExternalTypeGrain>(0);
            await grainWithEnumTypeParam.GetEnumModel();
        }
    }
}

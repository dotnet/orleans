using Xunit;
using Xunit.Abstractions;
using Orleans.Runtime.Development;
using Orleans.TestingHost;
using TestExtensions;
using TestExtensions.Runners;

namespace DefaultCluster.Tests
{
    [TestCategory("BVT"), TestCategory("Functional"), TestCategory("Lease")]
    public class GoldenPathInMemoryLeaseProviderTests : GoldenPathLeaseProviderTestRunner, IClassFixture<GoldenPathInMemoryLeaseProviderTests.Fixture>
    {
        public GoldenPathInMemoryLeaseProviderTests(Fixture fixture, ITestOutputHelper output)
            : base(new InMemoryLeaseProvider(fixture.GrainFactory), output)
        {
        }

        public class Fixture : BaseTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.ConfigureLegacyConfiguration();
            }
        }
    }
}

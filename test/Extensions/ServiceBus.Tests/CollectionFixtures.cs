using Tester;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests
{
    // Assembly collections must be defined once in each assembly
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }
    

    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }

    public abstract class BaseEventHubTestClusterFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForEventHub();
        }
    }
}

using TestExtensions;
using Xunit;

namespace GoogleUtils.Tests.Streaming
{
    // Assembly collections must be defined once in each assembly
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }

    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }
}

using TestExtensions;
using Xunit;

namespace NonSilo.Tests
{
    /// <summary>
    /// Defines the test collection for DefaultCluster tests in NonSilo.Tests.
    /// This collection ensures all tests share the same cluster instance for better performance.
    /// </summary>
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }

    /// <summary>
    /// Defines the test collection for TestEnvironmentFixture.
    /// This collection provides shared test environment setup across multiple test classes.
    /// </summary>
    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }
}

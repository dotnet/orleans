using TestExtensions;
using Xunit;

namespace DefaultCluster.Tests
{
    /// <summary>
    /// Defines the test collection for DefaultCluster tests.
    /// This collection ensures all tests share the same cluster instance for better performance.
    /// Tests in this collection run sequentially to avoid conflicts.
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

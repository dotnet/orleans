using TestExtensions;
using Xunit;

namespace AWSUtils.Tests
{
    // Assembly collections must be defined once in each assembly
    
    /// <summary>
    /// Defines a test collection for tests that require a default Orleans cluster setup.
    /// Tests in this collection share a single cluster instance for improved performance.
    /// </summary>
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }

    /// <summary>
    /// Defines a test collection for tests that require shared test environment configuration.
    /// Provides AWS-specific test environment setup and resources.
    /// </summary>
    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }
}

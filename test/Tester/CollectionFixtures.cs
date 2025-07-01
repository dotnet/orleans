using TestExtensions;
using Xunit;

namespace Tester
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
    /// Provides core Orleans test environment setup and resources.
    /// </summary>
    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }

    /// <summary>
    /// Base test cluster fixture for Azure-based integration tests.
    /// Ensures Azure Storage connectivity is available before running tests.
    /// </summary>
    public abstract class BaseAzureTestClusterFixture : BaseTestClusterFixture
    {
        protected override void CheckPreconditionsOrThrow()
        {
            base.CheckPreconditionsOrThrow();
            TestUtils.CheckForAzureStorage();
        }
    }
}

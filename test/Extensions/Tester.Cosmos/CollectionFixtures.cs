using TestExtensions;
using Xunit;

namespace Consul.Cosmos
{
    // Assembly collections must be defined once in each assembly

    /// <summary>
    /// Defines a test collection for tests that require shared test environment configuration.
    /// Provides Azure Cosmos DB specific test environment setup and resources.
    /// </summary>
    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }
}
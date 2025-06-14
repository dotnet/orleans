using TestExtensions;
using Xunit;

namespace Tester.Redis
{
    // Assembly collections must be defined once in each assembly

    /// <summary>
    /// Defines a test collection for tests that require shared test environment configuration.
    /// Provides Redis-specific test environment setup using a common fixture.
    /// </summary>
    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<CommonFixture> { }
}
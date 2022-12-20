using TestExtensions;
using Xunit;

namespace Consul.AzureCosmos
{
    // Assembly collections must be defined once in each assembly

    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }
}
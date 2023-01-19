using TestExtensions;
using Xunit;

namespace Tester.Redis
{
    // Assembly collections must be defined once in each assembly

    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<CommonFixture> { }
}
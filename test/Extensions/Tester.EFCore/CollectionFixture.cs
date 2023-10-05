using TestExtensions;
using Xunit;

namespace Tester.EFCore;

public class CollectionFixture
{
    // Assembly collections must be defined once in each assembly
    [CollectionDefinition(TestEnvironmentFixture.DefaultCollection)]
    public class TestEnvironmentFixtureCollection : ICollectionFixture<TestEnvironmentFixture> { }
}
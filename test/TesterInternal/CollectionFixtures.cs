using Tester;
using Xunit;

namespace UnitTests
{
    // Assembly collections might be defined once in each assembly
    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }
}

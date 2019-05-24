using Xunit;

namespace Grains.Tests
{
    [CollectionDefinition(nameof(ClusterCollection))]
    public class ClusterCollection : ICollectionFixture<ClusterFixture>
    {
    }
}

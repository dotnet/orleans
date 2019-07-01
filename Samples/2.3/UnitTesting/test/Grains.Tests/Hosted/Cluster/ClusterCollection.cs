using Xunit;

namespace Grains.Tests.Hosted.Cluster
{
    [CollectionDefinition(nameof(ClusterCollection))]
    public class ClusterCollection : ICollectionFixture<ClusterFixture>
    {
    }
}

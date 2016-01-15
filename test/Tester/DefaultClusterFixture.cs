using Orleans.TestingHost;
using Xunit;

namespace Tester
{
    public class DefaultClusterFixture : BaseClusterFixture
    {
        public DefaultClusterFixture()
            : base(new TestingSiloHost(true))
        {
        }
    }

    [CollectionDefinition("DefaultCluster")]
    public class DefaultClusterTestCollection : ICollectionFixture<DefaultClusterFixture> { }
}

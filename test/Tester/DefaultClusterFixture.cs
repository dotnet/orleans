using Orleans.TestingHost;

namespace Tester
{
    public class DefaultClusterFixture : BaseClusterFixture
    {
        public DefaultClusterFixture()
            : base(new TestingSiloHost(true))
        {
        }
    }
}

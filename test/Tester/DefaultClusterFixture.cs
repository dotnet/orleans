using Orleans.TestingHost;

namespace Tester
{
    public class DefaultClusterFixture : BaseClusterFixture
    {
        protected override TestingSiloHost CreateClusterHost()
        {
            return new TestingSiloHost(true);
        }
    }
}

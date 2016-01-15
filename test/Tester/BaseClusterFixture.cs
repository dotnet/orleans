using System;
using Orleans.TestingHost;

namespace Tester
{
    public abstract class BaseClusterFixture : IDisposable
    {
        protected BaseClusterFixture(TestingSiloHost hostedCluster)
        {
            this.HostedCluster = hostedCluster;
        }

        public TestingSiloHost HostedCluster { get; private set; }        

        public virtual void Dispose()
        {
            this.HostedCluster.StopAllSilos();
        }
    }
}
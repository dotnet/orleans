using System;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Tester
{
    public abstract class BaseClusterFixture : IDisposable
    {
        static BaseClusterFixture()
        {
            SerializationManager.InitializeForTesting();
        }

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
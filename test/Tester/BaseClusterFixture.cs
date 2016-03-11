using System;
using Orleans;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Tester
{
    public abstract class BaseClusterFixture : IDisposable
    {
        protected BaseClusterFixture()
        {
            GrainClient.Uninitialize();
            SerializationManager.InitializeForTesting();
            var hostedCluster = CreateClusterHost();
            this.HostedCluster = hostedCluster;
        }

        protected abstract TestingSiloHost CreateClusterHost();

        public TestingSiloHost HostedCluster { get; private set; }        

        public virtual void Dispose()
        {
            this.HostedCluster.StopAllSilos();
        }
    }
}
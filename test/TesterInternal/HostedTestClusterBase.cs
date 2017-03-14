using System;
using Orleans;
using Orleans.TestingHost;
using TestExtensions;

namespace UnitTests
{
    public abstract class BaseClusterFixture : IDisposable
    {
        protected BaseClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
            GrainClient.Uninitialize();
            var hostedCluster = CreateClusterHost();
            this.HostedCluster = hostedCluster;
        }

        protected abstract TestingSiloHost CreateClusterHost();

        public TestingSiloHost HostedCluster { get; private set; }

        public IGrainFactory GrainFactory => this.HostedCluster.GrainFactory;

        public virtual void Dispose()
        {
            HostedCluster?.StopAllSilos();
        }
    }
}

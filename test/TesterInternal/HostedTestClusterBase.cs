using System;
using Orleans;
using Orleans.Serialization;
using Orleans.TestingHost;
using Tester;
using TestExtensions;

namespace UnitTests
{
    public abstract class BaseClusterFixture : IDisposable
    {
        protected BaseClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
            GrainClient.Uninitialize();
            SerializationManager.InitializeForTesting();
            var hostedCluster = CreateClusterHost();
            this.HostedCluster = hostedCluster;
        }

        protected abstract TestingSiloHost CreateClusterHost();

        public TestingSiloHost HostedCluster { get; private set; }

        public virtual void Dispose()
        {
            HostedCluster?.StopAllSilos();
        }
    }
}

using System;
using Orleans;
using Orleans.Serialization;
using Orleans.TestingHost;
using UnitTests.Tester;

namespace UnitTests
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
            HostedCluster?.StopAllSilos();
        }
    }

    public abstract class HostedTestClusterPerTest : OrleansTestingBase, IDisposable
    {
        protected TestingSiloHost HostedCluster { get; private set; }

        public HostedTestClusterPerTest()
        {
            GrainClient.Uninitialize();
            SerializationManager.InitializeForTesting();
            this.HostedCluster = this.CreateSiloHost();
        }

        public virtual TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(true);
        }

        public virtual void Dispose()
        {
            HostedCluster?.StopAllSilos();
        }
    }
}

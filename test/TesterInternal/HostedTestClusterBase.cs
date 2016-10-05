using System;
using Orleans;
using Orleans.Serialization;
using Orleans.TestingHost;
using Tester;
using UnitTests.Tester;

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

    public abstract class HostedTestClusterPerTest : OrleansTestingBase, IDisposable
    {
        protected TestCluster HostedCluster { get; private set; }

        public HostedTestClusterPerTest()
        {
            TestDefaultConfiguration.InitializeDefaults();

            GrainClient.Uninitialize();
            SerializationManager.InitializeForTesting();
            var testCluster = CreateTestCluster();
            if (testCluster.Primary == null)
            {
                testCluster.Deploy();
            }
            this.HostedCluster = testCluster;
        }

        public virtual TestCluster CreateTestCluster()
        {
            return new TestCluster();
        }

        public virtual void Dispose()
        {
            HostedCluster?.StopAllSilos();

            //foreach (var silo in HostedCluster.GetActiveSilos())
            //{
            //    HostedCluster.KillSilo(silo);
            //}

        }
    }
}

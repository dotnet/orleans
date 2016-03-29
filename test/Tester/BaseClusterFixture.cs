using System;
using Orleans;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Tester
{
    public abstract class BaseTestClusterFixture : IDisposable
    {
        static BaseTestClusterFixture()
        {
            TestClusterOptions.DefaultTraceToConsole = false;
        }

        protected BaseTestClusterFixture()
        {
            GrainClient.Uninitialize();
            SerializationManager.InitializeForTesting();
            var testCluster = CreateTestCluster();
            if (testCluster.Primary == null)
            {
                testCluster.Deploy();
            }
            this.HostedCluster = testCluster;
        }

        protected abstract TestCluster CreateTestCluster();

        public TestCluster HostedCluster { get; private set; }

        public virtual void Dispose()
        {
            this.HostedCluster.StopAllSilos();
        }
    }
}
using System;
using Orleans;
using Orleans.TestingHost;

namespace TestExtensions
{
    public abstract class BaseTestClusterFixture : IDisposable
    {
        private static int defaultsAreInitialized = 0;

        static BaseTestClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
        }

        protected BaseTestClusterFixture()
        {
            GrainClient.Uninitialize();
            var testCluster = CreateTestCluster();
            if (testCluster.Primary == null)
            {
                testCluster.Deploy();
            }
            this.HostedCluster = testCluster;
        }

        protected abstract TestCluster CreateTestCluster();

        public TestCluster HostedCluster { get; }

        public IGrainFactory GrainFactory => this.HostedCluster.GrainFactory;

        public virtual void Dispose()
        {
            this.HostedCluster.StopAllSilos();
        }
    }
}
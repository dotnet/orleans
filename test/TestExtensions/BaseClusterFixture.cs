using System;
using Orleans;
using Orleans.TestingHost;

namespace TestExtensions
{
    using Orleans.Runtime;

    public abstract class BaseTestClusterFixture : IDisposable
    {
        static BaseTestClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
        }

        protected BaseTestClusterFixture()
        {
            var testCluster = CreateTestCluster();
            if (testCluster?.Primary == null)
            {
                testCluster?.Deploy();
            }
            this.HostedCluster = testCluster;
        }

        protected abstract TestCluster CreateTestCluster();

        public TestCluster HostedCluster { get; }

        public IGrainFactory GrainFactory => this.HostedCluster.GrainFactory;

        public IClusterClient Client => this.HostedCluster.Client;

        public Logger Logger => this.Client.Logger;

        public virtual void Dispose()
        {
            this.HostedCluster?.StopAllSilos();
        }
    }
}
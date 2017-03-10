using System;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;
using Xunit;

namespace TestExtensions
{
    /// <summary>
    /// Base class that ensures a silo cluster is started with the default configuration, and avoids restarting it if the previous test used the same default base.
    /// </summary>
    [Collection("DefaultCluster")]
    public abstract class HostedTestClusterEnsureDefaultStarted : OrleansTestingBase
    {
        protected TestCluster HostedCluster { get; private set; }

        protected IGrainFactory GrainFactory => this.HostedCluster.GrainFactory;

        protected IClusterClient Client => this.HostedCluster.Client;
        protected Logger Logger => this.Client.Logger;
        protected Logger logger => this.Logger;

        public HostedTestClusterEnsureDefaultStarted(DefaultClusterFixture fixture)
        {
            this.HostedCluster = fixture.HostedCluster;
        }
    }

    public abstract class TestClusterPerTest : OrleansTestingBase, IDisposable
    {
        static TestClusterPerTest()
        {
            TestDefaultConfiguration.InitializeDefaults();
        }

        protected TestCluster HostedCluster { get; private set; }

        internal IInternalClusterClient InternalClient => (IInternalClusterClient)this.Client;

        public IClusterClient Client => this.HostedCluster.Client;

        protected IGrainFactory GrainFactory => this.Client;

        protected Logger Logger => this.Client.Logger;
        protected Logger logger => this.Logger;

        public TestClusterPerTest()
        {
            var testCluster = this.CreateTestCluster();
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
            this.HostedCluster?.StopAllSilos();
        }
    }
}

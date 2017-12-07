using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        protected ILogger Logger => this.Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        protected ILogger logger => this.Logger;

        protected HostedTestClusterEnsureDefaultStarted(DefaultClusterFixture fixture)
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

        protected ILogger Logger => this.logger;
        protected ILogger logger;

        public TestClusterPerTest()
        {
            var testCluster = this.CreateTestCluster();
            if (testCluster.Primary == null)
            {
                testCluster.Deploy();
            }
            this.HostedCluster = testCluster;
            this.logger = this.Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
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

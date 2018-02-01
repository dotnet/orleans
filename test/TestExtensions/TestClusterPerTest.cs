using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.TestingHost;

namespace TestExtensions
{
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

        protected TestClusterPerTest()
        {
            var builder = new TestClusterBuilder();
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            builder.ConfigureLegacyConfiguration();
            this.ConfigureTestCluster(builder);

            var testCluster = builder.Build();
            if (testCluster.Primary == null)
            {
                testCluster.Deploy();
            }
            this.HostedCluster = testCluster;
            this.logger = this.Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        }

        protected virtual void ConfigureTestCluster(TestClusterBuilder builder)
        {
        }

        public virtual void Dispose()
        {
            this.HostedCluster?.StopAllSilos();
        }
    }
}
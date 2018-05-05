using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
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
        protected DefaultClusterFixture Fixture { get; private set; }
        protected TestCluster HostedCluster => this.Fixture.HostedCluster;

        protected IGrainFactory GrainFactory => this.HostedCluster.GrainFactory;

        protected IClusterClient Client => this.HostedCluster.Client;
        protected ILogger Logger => this.Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");

        protected HostedTestClusterEnsureDefaultStarted(DefaultClusterFixture fixture)
        {
            this.Fixture = fixture;
        }
    }
}

using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.TestingHost;

namespace TestExtensions
{
    public abstract class BaseTestClusterFixture : Xunit.IAsyncLifetime
    {
        private readonly ExceptionDispatchInfo preconditionsException;

        static BaseTestClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
        }

        protected BaseTestClusterFixture()
        {
            try
            {
                CheckPreconditionsOrThrow();
            }
            catch (Exception ex)
            {
                this.preconditionsException = ExceptionDispatchInfo.Capture(ex);
                return;
            }
        }

        public void EnsurePreconditionsMet()
        {
            this.preconditionsException?.Throw();
        }

        protected virtual void CheckPreconditionsOrThrow() { }

        protected virtual void ConfigureTestCluster(TestClusterBuilder builder)
        {
        }

        public TestCluster HostedCluster { get; private set; }

        public IGrainFactory GrainFactory => this.HostedCluster?.GrainFactory;

        public IClusterClient Client => this.HostedCluster?.Client;

        public ILogger Logger { get; private set; }
        
        public string GetClientServiceId() => Client.ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId;

        public virtual async Task InitializeAsync()
        {
            this.EnsurePreconditionsMet();
            var builder = new TestClusterBuilder();
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            this.ConfigureTestCluster(builder);

            var testCluster = builder.Build();
            if (testCluster.Primary == null)
            {
                await testCluster.DeployAsync().ConfigureAwait(false);
            }

            this.HostedCluster = testCluster;
            this.Logger = this.Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        }

        public virtual async Task DisposeAsync()
        {
            var cluster = this.HostedCluster;
            if (cluster is null) return;

            try
            {
                await cluster.StopAllSilosAsync().ConfigureAwait(false);
            }
            finally
            {
                await cluster.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
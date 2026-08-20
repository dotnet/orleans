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
        private readonly ExceptionDispatchInfo? preconditionsException;
        private TestCluster? hostedCluster;

        protected bool PreconditionsMet => this.preconditionsException is null;

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

        public TestCluster HostedCluster
        {
            get
            {
                this.EnsurePreconditionsMet();
                return this.hostedCluster ?? throw new InvalidOperationException("The test cluster has not been initialized.");
            }
            private set => this.hostedCluster = value;
        }

        public IGrainFactory GrainFactory
        {
            get
            {
                this.EnsurePreconditionsMet();
                return this.HostedCluster.GrainFactory;
            }
        }

        public IClusterClient Client
        {
            get
            {
                this.EnsurePreconditionsMet();
                return this.HostedCluster.Client;
            }
        }

        public ILogger Logger { get; private set; } = null!;
        
        public string GetClientServiceId() => Client.ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId;

        public virtual async ValueTask InitializeAsync()
        {
            if (!this.PreconditionsMet)
            {
                return;
            }

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

        public virtual async ValueTask DisposeAsync()
        {
            var cluster = this.hostedCluster;
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
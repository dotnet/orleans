using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.TestingHost;

namespace TestExtensions
{
    public abstract class TestClusterPerTest : OrleansTestingBase, Xunit.IAsyncLifetime
    {
        private readonly ExceptionDispatchInfo preconditionsException;
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

        public virtual async Task InitializeAsync()
        {
            var builder = new TestClusterBuilder();
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            this.ConfigureTestCluster(builder);

            var testCluster = builder.Build();
            if (testCluster.Primary == null)
            {
                await testCluster.DeployAsync().ConfigureAwait(false);
            }

            this.HostedCluster = testCluster;
            this.logger = this.Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
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
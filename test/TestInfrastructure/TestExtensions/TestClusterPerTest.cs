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

        internal IInternalClusterClient InternalClient => (IInternalClusterClient)Client;

        public IClusterClient Client => HostedCluster.Client;

        protected IGrainFactory GrainFactory => Client;

        protected ILogger Logger => logger;
        protected ILogger logger;

        protected TestClusterPerTest()
        {
            try
            {
                CheckPreconditionsOrThrow();
            }
            catch (Exception ex)
            {
                preconditionsException = ExceptionDispatchInfo.Capture(ex);
                return;
            }
        }

        public void EnsurePreconditionsMet()
        {
            preconditionsException?.Throw();
        }

        protected virtual void CheckPreconditionsOrThrow() { }

        protected virtual void ConfigureTestCluster(TestClusterBuilder builder)
        {
        }

        public virtual async Task InitializeAsync()
        {
            var builder = new TestClusterBuilder();
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            ConfigureTestCluster(builder);

            var testCluster = builder.Build();
            if (testCluster.Primary == null)
            {
                await testCluster.DeployAsync().ConfigureAwait(false);
            }

            HostedCluster = testCluster;
            logger = Client.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        }

        public virtual async Task DisposeAsync()
        {
            var cluster = HostedCluster;
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
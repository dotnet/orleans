using System;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.TestingHost;

namespace TestExtensions
{
    public abstract class BaseTestClusterFixture : IDisposable
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

            var builder = new TestClusterBuilder();
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            builder.ConfigureLegacyConfiguration();
            ConfigureTestCluster(builder);

            var testCluster = builder.Build();
            if (testCluster?.Primary == null)
            {
                testCluster?.Deploy();
            }
            this.HostedCluster = testCluster;
            this.Logger = this.Client?.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        }

        public void EnsurePreconditionsMet()
        {
            this.preconditionsException?.Throw();
        }

        protected virtual void CheckPreconditionsOrThrow() { }

        protected virtual void ConfigureTestCluster(TestClusterBuilder builder)
        {
        }

        public TestCluster HostedCluster { get; }

        public IGrainFactory GrainFactory => this.HostedCluster?.GrainFactory;

        public IClusterClient Client => this.HostedCluster?.Client;

        public ILogger Logger { get; }
        
        public virtual void Dispose()
        {
            this.HostedCluster?.StopAllSilos();
        }

        public string GetClientServiceId() => Client.ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value.ServiceId;
    }
}
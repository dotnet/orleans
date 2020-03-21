using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;

using System.Threading.Tasks;

namespace TestExtensions
{
    public class DefaultClusterFixture : IDisposable, Xunit.IAsyncLifetime
    {
        static DefaultClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
        }

        public DefaultClusterFixture()
        {
            var builder = new TestClusterBuilder();
            TestDefaultConfiguration.ConfigureTestCluster(builder);
            
            builder.AddSiloBuilderConfigurator<SiloHostConfigurator>();

            var testCluster = builder.Build();
            if (testCluster?.Primary == null)
            {
                testCluster?.Deploy();
            }

            this.HostedCluster = testCluster;
            this.Logger = this.Client?.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        }
        
        public TestCluster HostedCluster { get; }

        public IGrainFactory GrainFactory => this.HostedCluster?.GrainFactory;

        public IClusterClient Client => this.HostedCluster?.Client;

        public ILogger Logger { get; }

        public virtual void Dispose()
        {
            this.HostedCluster?.StopAllSilos();
        }

        public Task InitializeAsync()
        {
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            try
            {
                if (this.HostedCluster is TestCluster cluster)
                {
                    await cluster.StopAllSilosAsync();
                }
            }
            finally
            {
                this.HostedCluster?.Dispose();
            }
        }

        public class SiloHostConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder
                    .UseInMemoryReminderService()
                    .AddMemoryGrainStorageAsDefault()
                    .AddMemoryGrainStorage("MemoryStore");
            }
        }
    }
}

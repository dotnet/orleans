using System;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Grains.Tests
{
    public class ClusterFixture : IDisposable
    {
        private readonly TestCluster cluster;

        public ClusterFixture()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();

            cluster = builder.Build();
            cluster.Deploy();
        }

        private class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault();
                hostBuilder.UseInMemoryReminderService();
            }
        }

        public void Dispose() => cluster.StopAllSilos();
    }
}

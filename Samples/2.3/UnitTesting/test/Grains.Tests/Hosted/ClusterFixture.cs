using System;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Grains.Tests.Hosted
{
    public class ClusterFixture : IDisposable
    {
        public TestCluster Cluster { get; }

        public ClusterFixture()
        {
            var builder = new TestClusterBuilder();
            builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();

            Cluster = builder.Build();
            Cluster.Deploy();
        }

        private class SiloBuilderConfigurator : ISiloBuilderConfigurator
        {
            public void Configure(ISiloHostBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorageAsDefault();
                hostBuilder.UseInMemoryReminderService();
            }
        }

        public void Dispose() => Cluster.StopAllSilos();
    }
}

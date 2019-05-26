using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.Timers;

namespace Grains.Tests.Hosted
{
    public class ClusterFixture : IDisposable
    {
        /// <summary>
        /// Identifier for this test cluster instance.
        /// This facilitates parallel testing with multiple clusters that need the fake services.
        /// </summary>
        public string TestClusterId { get; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Exposes the shared cluster for unit tests to use.
        /// </summary>
        public TestCluster Cluster { get; }

        /// <summary>
        /// Exposes the fake timer registry for unit tests to use.
        /// </summary>
        public IEnumerable<FakeTimerRegistry> TimerRegistries => AllTimerRegistries[TestClusterId];

        /// <summary>
        /// Keeps all the fake timer registries in use by different clusters.
        /// This facilitates parallel unit testing.
        /// </summary>
        public static ConcurrentDictionary<string, ConcurrentQueue<FakeTimerRegistry>> AllTimerRegistries { get; } = new ConcurrentDictionary<string, ConcurrentQueue<FakeTimerRegistry>>();

        public ClusterFixture()
        {
            // prepare to receive the timer registries for each silo
            AllTimerRegistries[TestClusterId] = new ConcurrentQueue<FakeTimerRegistry>();

            var builder = new TestClusterBuilder();

            // add the cluster id for this instance
            // this allows the silos to safely lookup shared data for this cluster deployment
            // without this we can only share data via static properties and that messes up parallel testing
            builder.ConfigureHostConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>()
                {
                    { nameof(TestClusterId), TestClusterId }
                });
            });

            // a configurator allows the silos to configure themselves
            // at this time, configurators cannot take injected parameters
            // therefore we must other means of sharing objects as you can see above
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
                hostBuilder.ConfigureServices(services =>
                {
                    services.AddSingleton<FakeTimerRegistry>();
                    services.AddSingleton<ITimerRegistry>(_ => _.GetService<FakeTimerRegistry>());
                });
                hostBuilder.UseServiceProviderFactory(services =>
                {
                    var provider = services.BuildServiceProvider();
                    var config = provider.GetService<IConfiguration>();

                    var registry = provider.GetService<FakeTimerRegistry>();
                    AllTimerRegistries[config[nameof(TestClusterId)]].Enqueue(registry);

                    return provider;
                });
            }
        }

        public void Dispose() => Cluster.StopAllSilos();
    }
}
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Storage;
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
        /// Keeps all the fake grain storage instances in use by different clusters.
        /// This facilitates parallel unit testing.
        /// </summary>
        public static ConcurrentDictionary<string, ConcurrentQueue<FakeGrainStorage>> GrainStorageGroups { get; } = new ConcurrentDictionary<string, ConcurrentQueue<FakeGrainStorage>>();

        /// <summary>
        /// Exposes the grain storage instances for unit tests to use.
        /// </summary>
        public IEnumerable<FakeGrainStorage> GrainStorageInstances => GrainStorageGroups[TestClusterId];

        /// <summary>
        /// Keeps all the fake timer registries in use by different clusters.
        /// This facilitates parallel unit testing.
        /// </summary>
        public static ConcurrentDictionary<string, ConcurrentQueue<FakeTimerRegistry>> TimerRegistryGroups { get; } = new ConcurrentDictionary<string, ConcurrentQueue<FakeTimerRegistry>>();

        /// <summary>
        /// Exposes the fake timer registry for unit tests to use.
        /// </summary>
        public IEnumerable<FakeTimerRegistry> TimerRegistryInstances => TimerRegistryGroups[TestClusterId];

        public ClusterFixture()
        {
            // prepare to receive the grain storage instances from each silo
            GrainStorageGroups[TestClusterId] = new ConcurrentQueue<FakeGrainStorage>();

            // prepare to receive the timer registry instances from each silo
            TimerRegistryGroups[TestClusterId] = new ConcurrentQueue<FakeTimerRegistry>();

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
                hostBuilder.ConfigureServices(services =>
                {
                    // add the fake storage provider as default in a way that lets us extract it afterwards
                    services.AddSingleton(_ => new FakeGrainStorage());
                    services.AddSingleton<IGrainStorage>(_ => _.GetService<FakeGrainStorage>());

                    // add the fake timer registry in a way that lets us extract it afterwards
                    services.AddSingleton<FakeTimerRegistry>();
                    services.AddSingleton<ITimerRegistry>(_ => _.GetService<FakeTimerRegistry>());
                });
                hostBuilder.UseServiceProviderFactory(services =>
                {
                    var provider = services.BuildServiceProvider();
                    var config = provider.GetService<IConfiguration>();

                    // grab the cluster id that owns this silo
                    var clusterId = config[nameof(TestClusterId)];

                    // extract the fake storage provider for this silo
                    var storage = provider.GetService<FakeGrainStorage>();
                    GrainStorageGroups[clusterId].Enqueue(storage);

                    // extract the fake timer registry for this silo
                    var registry = provider.GetService<FakeTimerRegistry>();
                    TimerRegistryGroups[clusterId].Enqueue(registry);

                    return provider;
                });
            }
        }

        public void Dispose() => Cluster.StopAllSilos();
    }
}
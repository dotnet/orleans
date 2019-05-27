using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Grains.Tests.Hosted.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using Orleans.Timers;

namespace Grains.Tests.Hosted.Cluster
{
    public class ClusterFixture : IDisposable
    {
        /// <summary>
        /// Identifier for this test cluster instance to facilitate parallel testing with multiple clusters that need fake services.
        /// </summary>
        public string TestClusterId { get; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Exposes the shared cluster for unit tests to use.
        /// </summary>
        public TestCluster Cluster { get; }

        /// <summary>
        /// Keeps all the fake grain storage instances in use by different clusters to facilitate parallel unit testing.
        /// </summary>
        public static ConcurrentDictionary<string, ConcurrentBag<FakeGrainStorage>> GrainStorageGroups { get; } = new ConcurrentDictionary<string, ConcurrentBag<FakeGrainStorage>>();

        /// <summary>
        /// Gets the fake grain storage item for the given grain by searching across all silos.
        /// </summary>
        public IGrainState GetGrainState(Type implementationType, string name, IGrain grain)
        {
            return GrainStorageGroups[TestClusterId]
                .SelectMany(_ => _.Storage)
                .Where(_ => _.Key.Item1 == $"{implementationType.FullName}{(name == null ? "" : $",{typeof(PersistentGrain).Namespace}.{name}")}")
                .Where(_ => _.Key.Item2.Equals((GrainReference)grain))
                .Select(_ => _.Value)
                .SingleOrDefault();
        }

        /// <summary>
        /// Keeps all the fake timer registries in use by different clusters to facilitate parallel unit testing.
        /// </summary>
        public static ConcurrentDictionary<string, ConcurrentBag<FakeTimerRegistry>> TimerRegistryGroups { get; } = new ConcurrentDictionary<string, ConcurrentBag<FakeTimerRegistry>>();

        /// <summary>
        /// Gets all the fake timers for the target grain across all silos.
        /// </summary>
        public IEnumerable<FakeTimerEntry> GetTimers(IGrain grain)
        {
            return TimerRegistryGroups[TestClusterId]
                .SelectMany(_ => _.GetAll())
                .Where(_ => _.Grain.GrainReference.Equals((GrainReference)grain));
        }

        /// <summary>
        /// Keeps all the fake reminder registries in use by different clusters to facilitate parallel unit testing.
        /// </summary>
        public static ConcurrentDictionary<string, ConcurrentBag<FakeReminderRegistry>> ReminderRegistryGroups { get; } = new ConcurrentDictionary<string, ConcurrentBag<FakeReminderRegistry>>();

        /// <summary>
        /// Gets the target fake reminder by searching across all silos.
        /// </summary>
        public FakeReminder GetReminder(IGrain grain, string name)
        {
            return ReminderRegistryGroups[TestClusterId]
                .Select(_ => _.GetReminder((GrainReference)grain, name).Result)
                .Where(_ => _ != null)
                .SingleOrDefault();
        }

        public ClusterFixture()
        {
            // prepare to receive the fake services from individual silos
            GrainStorageGroups[TestClusterId] = new ConcurrentBag<FakeGrainStorage>();
            TimerRegistryGroups[TestClusterId] = new ConcurrentBag<FakeTimerRegistry>();
            ReminderRegistryGroups[TestClusterId] = new ConcurrentBag<FakeReminderRegistry>();

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

                    // add the fake reminder registry in a way that lets us extract it afterwards
                    services.AddSingleton<FakeReminderRegistry>();
                    services.AddSingleton<IReminderRegistry>(_ => _.GetService<FakeReminderRegistry>());
                });

                hostBuilder.UseServiceProviderFactory(services =>
                {
                    var provider = services.BuildServiceProvider();
                    var config = provider.GetService<IConfiguration>();

                    // grab the cluster id that owns this silo
                    var clusterId = config[nameof(TestClusterId)];

                    // extract the fake services from the silo so unit tests can access them
                    GrainStorageGroups[clusterId].Add(provider.GetService<FakeGrainStorage>());
                    TimerRegistryGroups[clusterId].Add(provider.GetService<FakeTimerRegistry>());
                    ReminderRegistryGroups[clusterId].Add(provider.GetService<FakeReminderRegistry>());

                    return provider;
                });
            }
        }

        public void Dispose() => Cluster.StopAllSilos();
    }
}
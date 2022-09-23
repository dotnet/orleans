using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.CollectionGuards;
using Orleans.Serialization.TypeSystem;
using Orleans.Statistics;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests
{
    /// <summary>
    /// Implementation of IAppEnvironmentStatistics that can be used to test the behavior of the system
    /// by manually setting the used memory.
    /// </summary>
    class AppEnvironmentStatistics : IAppEnvironmentStatistics
    {
        public long? MemoryUsage { get; set; } = 1337;
    }

    /// <summary>
    /// An implement of IAppEnvironmentStatistics that returns a series of values for memory usage.
    ///
    /// It can be queried on the Index property to see how many times it has been called.
    ///
    /// Currently, the index is increased by a version of the guard that is made for testing.
    /// </summary>
    class MultipleAppEnvironmentStatistics : IAppEnvironmentStatistics
    {
        private readonly IList<long> _memoryUsage;

        public int Index { get; set; } = 0;

        public long BaseMemoryUsage { get; }

        public MultipleAppEnvironmentStatistics(
            IList<long> memoryUsage,
            long baseMemoryUsage = 0)
        {
            _memoryUsage = memoryUsage;
            BaseMemoryUsage = baseMemoryUsage;
        }

        public long? MemoryUsage => Index >= _memoryUsage.Count
            ? BaseMemoryUsage
            : _memoryUsage[Index];
    }

    /// <summary>
    /// An implementation of <see cref="IGrainCollectionGuard"/> that has the ability to change the perceived
    /// GC footprint of as presented to a silo.
    /// </summary>
    class PredictableProcessMemoryGrainCollectionGuard :
        IGrainCollectionGuard
    {
        private readonly MultipleAppEnvironmentStatistics _multipleAppEnvironmentStatistics;
        private readonly ProcessMemoryGrainCollectionGuard _processMemoryGrainCollectionGuard;

        public PredictableProcessMemoryGrainCollectionGuard(
            IAppEnvironmentStatistics appEnvironmentStatistics,
            IOptions<GrainCollectionOptions> grainCollectionOptions)
        {
            _multipleAppEnvironmentStatistics = (MultipleAppEnvironmentStatistics)appEnvironmentStatistics;
            _processMemoryGrainCollectionGuard =
                new ProcessMemoryGrainCollectionGuard(appEnvironmentStatistics, grainCollectionOptions);
        }

        public bool ShouldCollect()
        {
            var shouldCollect = _processMemoryGrainCollectionGuard.ShouldCollect();
            _multipleAppEnvironmentStatistics.Index++;
            return shouldCollect;
        }
    }

    /// <summary>
    /// Implementation of IHostEnvironmentStatistics that can be used to test the behavior of the system
    /// by manually setting the used and available memory and CPU.
    /// </summary>
    class HostEnvironmentStatistics : IHostEnvironmentStatistics
    {
        public long? TotalPhysicalMemory { get; set; }
        public float? CpuUsage { get; set; }
        public long? AvailableMemory { get; set; }
    }

    public class PredictableGCStatisticsCollectorTests : OrleansTestingBase, IAsyncLifetime
    {
        private static readonly TimeSpan DEFAULT_COLLECTION_QUANTUM = TimeSpan.FromSeconds(10);
        private static readonly long GCThreshold = 1_000_000_000;
        private static readonly TimeSpan DEFAULT_IDLE_TIMEOUT = DEFAULT_COLLECTION_QUANTUM + TimeSpan.FromSeconds(1);
        private static readonly TimeSpan WAIT_TIME = DEFAULT_IDLE_TIMEOUT.Multiply(3.0);

        private TestCluster testCluster;

        private ILogger logger;

        private async Task Initialize(
            TimeSpan collectionAgeLimit,
            TimeSpan quantum,
            long collectionGCMemoryThreshold)
        {
            var builder = new TestClusterBuilder(1);
            builder.Properties["CollectionQuantum"] = quantum.ToString();
            builder.Properties["DefaultCollectionAgeLimit"] = collectionAgeLimit.ToString();
            builder.Properties["CollectionGCMemoryThreshold"] = collectionGCMemoryThreshold.ToString();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            testCluster = builder.Build();
            await testCluster.DeployAsync();
            this.logger =
                this.testCluster.Client.ServiceProvider.GetRequiredService<ILogger<ActivationCollectorTests>>();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var config = hostBuilder.GetConfiguration();
                var collectionAgeLimit = TimeSpan.Parse(config["DefaultCollectionAgeLimit"]);
                var quantum = TimeSpan.Parse(config["CollectionQuantum"]);
                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .ConfigureServices(services =>
                        {
                            services.Where(s => s.ServiceType == typeof(IConfigurationValidator)).ToList()
                                .ForEach(s => services.Remove(s));
                            services
                                .AddSingleton<IGrainCollectionGuard, PredictableProcessMemoryGrainCollectionGuard>()
                                .AddSingleton<IAppEnvironmentStatistics>(_ => new MultipleAppEnvironmentStatistics(
                                new List<long>
                                {
                                    1_100_000_000,
                                    1_100_000_000,
                                    1_100_000_000,
                                }, 900_000_000));
                            services.AddSingleton<IHostEnvironmentStatistics, HostEnvironmentStatistics>();
                        });
                    siloBuilder.Configure<GrainCollectionOptions>(options =>
                    {
                        options.CollectionAge = collectionAgeLimit;
                        options.CollectionQuantum = quantum;
                        options.CollectionGCMemoryThreshold = GCThreshold;
                        options.CollectionBatchSize = 50;
                        options.ClassSpecificCollectionAge = new Dictionary<string, TimeSpan>
                        {
                            [typeof(IdleActivationGcTestGrain2).FullName] = DEFAULT_IDLE_TIMEOUT,
                            [typeof(BusyActivationGcTestGrain2).FullName] = DEFAULT_IDLE_TIMEOUT,
                            [typeof(CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain).FullName] =
                                TimeSpan.FromSeconds(12),
                        };
                    });
                });
            }
        }

        Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

        private async Task Initialize(TimeSpan collectionAgeLimit)
        {
            await Initialize(collectionAgeLimit, DEFAULT_COLLECTION_QUANTUM, GCThreshold);
        }

        public async Task DisposeAsync()
        {
            if (testCluster is null) return;

            try
            {
                await testCluster.StopAllSilosAsync();
            }
            finally
            {
                await testCluster.DisposeAsync();
                testCluster = null;
            }
        }

        /// <summary>
        /// This tries to implement a deterministic system for testing the response of the guards to the GC statistics.
        ///
        /// I cannot get it to work quite yet, but I think the batch-collector in <see cref="ActivationCollector"/> is
        /// correctly implemented.
        /// </summary>
        [Fact(Skip="I cannot get this to work and need help. :)"), TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task CollectionShouldNotCollectActivationsUnderLowGCPressure()
        {
            await Initialize(DEFAULT_IDLE_TIMEOUT);

            var silo = testCluster.Primary as InProcessSiloHandle;
            Assert.NotNull(silo);

            var appEnvironmentStatistics = silo
                .SiloHost
                .Services
                .GetRequiredService<IAppEnvironmentStatistics>() as MultipleAppEnvironmentStatistics;
            Assert.NotNull(appEnvironmentStatistics);

            var predictableProcessMemoryGrainCollectionGuard = silo
                .SiloHost
                .Services
                .GetRequiredService<IGrainCollectionGuard>() as PredictableProcessMemoryGrainCollectionGuard;
            Assert.NotNull(predictableProcessMemoryGrainCollectionGuard);

            const int idleGrainCount = 500;
            const int busyGrainCount = 500;
            var idleGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain1));
            var busyGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(BusyActivationGcTestGrain1));

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain1> busyGrains = new List<IBusyActivationGcTestGrain1>();
            logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivations: activating {Count} busy grains.",
                busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g =
                    this.testCluster.GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }

            await Task.WhenAll(tasks0);
            bool[] quit = new bool[] { false };
            Func<Task> busyWorker =
                async () =>
                {
                    logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivations: busyWorker started");
                    List<Task> tasks1 = new List<Task>();
                    while (!quit[0])
                    {
                        foreach (var g in busyGrains)
                            tasks1.Add(g.Nop());
                        await Task.WhenAll(tasks1);
                    }
                };
            Task.Run(busyWorker).Ignore();

            logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivations: activating {Count} idle grains.",
                idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g =
                    this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }

            await Task.WhenAll(tasks0);

            int activationsCreated =
                await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName) +
                await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(idleGrainCount + busyGrainCount, activationsCreated);

            logger.LogInformation(
                "ActivationCollectorShouldNotCollectBusyActivations: grains activated; waiting {WaitSeconds} sec (activation GC idle timeout is {DefaultIdleTime} sec).",
                WAIT_TIME.TotalSeconds,
                DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME * 10);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain1).
            int idleActivationsNotCollected =
                await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName);
            int busyActivationsNotCollected =
                await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);

            // This is not possible to assert, because the timing of the test is not deterministic.
            // Assert.Equal(5, appEnvironmentStatistics.Index);

            Assert.Equal(busyGrainCount, busyActivationsNotCollected);
            Assert.Equal(400, idleActivationsNotCollected);

            quit[0] = true;
        }
    }
}
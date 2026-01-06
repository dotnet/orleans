using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using UnitTestGrains;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests
{
    /// <summary>
    /// Tests for the activation collector that manages grain activation lifecycle and garbage collection.
    /// Uses FakeTimeProvider for deterministic testing of idle time tracking.
    /// </summary>
    public class ActivationCollectorTests : OrleansTestingBase, IAsyncLifetime
    {
        private static readonly TimeSpan DEFAULT_COLLECTION_QUANTUM = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DEFAULT_IDLE_TIMEOUT = DEFAULT_COLLECTION_QUANTUM + TimeSpan.FromSeconds(1);
        /// <summary>
        /// Maximum time to wait for deactivations. This is a safety timeout for event-driven waiting.
        /// Tests should complete faster when using diagnostic events.
        /// </summary>
        private static readonly TimeSpan MAX_WAIT_TIME = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Shared FakeTimeProvider instance used by all silos. This allows tests to control
        /// virtual time for deterministic testing of idle time tracking.
        /// </summary>
        private static FakeTimeProvider _sharedTimeProvider;

        private TestCluster testCluster;

        private ILogger logger;
        private GrainDiagnosticObserver _diagnosticObserver;

        private async Task Initialize(TimeSpan collectionAgeLimit, TimeSpan quantum)
        {
            // Initialize the diagnostic observer before deploying the cluster
            // so it can capture all grain lifecycle events from the start
            _diagnosticObserver = GrainDiagnosticObserver.Create();

            // Create a FakeTimeProvider for deterministic time control
            _sharedTimeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

            var builder = new TestClusterBuilder(1);
            builder.Properties["CollectionQuantum"] = quantum.ToString();
            builder.Properties["DefaultCollectionAgeLimit"] = collectionAgeLimit.ToString();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            testCluster = builder.Build();
            await testCluster.DeployAsync();
            this.logger = this.testCluster.Client.ServiceProvider.GetRequiredService<ILogger<ActivationCollectorTests>>();
        }

        public class SiloConfigurator : IHostConfigurator
        {
            public void Configure(IHostBuilder hostBuilder)
            {
                var config = hostBuilder.GetConfiguration();
                var collectionAgeLimit = TimeSpan.Parse(config["DefaultCollectionAgeLimit"]!);
                var quantum = TimeSpan.Parse(config["CollectionQuantum"]!);
                hostBuilder.UseOrleans((ctx, siloBuilder) =>
                {
                    siloBuilder
                        .ConfigureServices(services => services.Where(s => s.ServiceType == typeof(IConfigurationValidator)).ToList().ForEach(s => services.Remove(s)));
                    
                    // Register the shared FakeTimeProvider for deterministic idle time tracking
                    if (_sharedTimeProvider != null)
                    {
                        siloBuilder.ConfigureServices(services => services.AddSingleton<TimeProvider>(_sharedTimeProvider));
                    }
                    
                    siloBuilder.Configure<GrainCollectionOptions>(options =>
                    {
                        options.CollectionAge = collectionAgeLimit;
                        options.CollectionQuantum = quantum;
                        options.ClassSpecificCollectionAge = new Dictionary<string, TimeSpan>
                        {
                            [typeof(IdleActivationGcTestGrain2).FullName!] = DEFAULT_IDLE_TIMEOUT,
                            [typeof(BusyActivationGcTestGrain2).FullName!] = DEFAULT_IDLE_TIMEOUT,
                            [typeof(CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain).FullName!] = TimeSpan.FromSeconds(12),
                        };
                    });
                });
            }
        }


        Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

        private async Task Initialize(TimeSpan collectionAgeLimit)
        {
            await Initialize(collectionAgeLimit, DEFAULT_COLLECTION_QUANTUM);
        }

        public async Task DisposeAsync()
        {
            _diagnosticObserver?.Dispose();

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

        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorForceCollection()
        {
            await Initialize(DEFAULT_IDLE_TIMEOUT);

            const int grainCount = 1000;
            var fullGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain1));

            List<Task> tasks = new List<Task>();
            logger.LogInformation("ActivationCollectorForceCollection: activating {Count} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            // Advance virtual time by 5 seconds so grains are considered idle for 5 seconds.
            // ForceActivationCollection(4 seconds) will collect grains idle for 4+ seconds.
            // Using FakeTimeProvider allows deterministic testing without real-time delays.
            _sharedTimeProvider!.Advance(TimeSpan.FromSeconds(5));

            var grain = this.testCluster.GrainFactory.GetGrain<IManagementGrain>(0);

            await grain.ForceActivationCollection(TimeSpan.FromSeconds(4));

            int activationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(0, activationsNotCollected);

            await grain.ForceActivationCollection(TimeSpan.FromSeconds(4));
        }

        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldCollectIdleActivations()
        {
            await Initialize(DEFAULT_IDLE_TIMEOUT);

            const int grainCount = 1000;
            var fullGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain1));

            List<Task> tasks = new List<Task>();
            logger.LogInformation("ActivationCollectorShouldCollectIdleActivations: activating {Count} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(grainCount, activationsCreated);

            logger.LogInformation(
                "ActivationCollectorShouldCollectIdleActivations: grains activated; waiting for {Count} deactivations (activation GC idle timeout is {DefaultIdleTime} sec).",
                grainCount,
                DEFAULT_IDLE_TIMEOUT.TotalSeconds);

            // Advance virtual time past the collection age limit so grains become stale.
            _sharedTimeProvider!.Advance(DEFAULT_IDLE_TIMEOUT + TimeSpan.FromSeconds(5));

            // Wait for all grains to be deactivated using event-driven approach
            // Pass the FakeTimeProvider to continue advancing time during polling
            await _diagnosticObserver.WaitForDeactivationCountAsync("IdleActivationGcTestGrain1", grainCount, MAX_WAIT_TIME, _sharedTimeProvider);

            int activationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(0, activationsNotCollected);
        }   

        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldNotCollectBusyActivations()
        {
            await Initialize(DEFAULT_IDLE_TIMEOUT);

            const int idleGrainCount = 500;
            const int busyGrainCount = 500;
            var idleGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain1));
            var busyGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(BusyActivationGcTestGrain1));

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain1> busyGrains = new List<IBusyActivationGcTestGrain1>();
            logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivations: activating {Count} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);
            bool[] quit = new bool[]{ false };
            async Task busyWorker()
            {
                logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivations: busyWorker started");
                List<Task> tasks1 = new List<Task>();
                while (!quit[0])
                {
                    foreach (var g in busyGrains)
                        tasks1.Add(g.Nop());
                    await Task.WhenAll(tasks1);
                }
            }
            Task.Run(busyWorker).Ignore();

            logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivations: activating {Count} idle grains.", idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName) + await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(idleGrainCount + busyGrainCount, activationsCreated);

            logger.LogInformation(
                "ActivationCollectorShouldNotCollectBusyActivations: grains activated; waiting for {Count} idle grain deactivations (activation GC idle timeout is {DefaultIdleTime} sec).",
                idleGrainCount,
                DEFAULT_IDLE_TIMEOUT.TotalSeconds);

            // Advance virtual time past the collection age limit so idle grains become stale.
            // Busy grains will continue making real-time requests, keeping them active.
            _sharedTimeProvider!.Advance(DEFAULT_IDLE_TIMEOUT + TimeSpan.FromSeconds(5));

            // Wait for all idle grains to be deactivated using event-driven approach
            // Pass the FakeTimeProvider to continue advancing time during polling
            await _diagnosticObserver.WaitForDeactivationCountAsync("IdleActivationGcTestGrain1", idleGrainCount, MAX_WAIT_TIME, _sharedTimeProvider);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain1).
            int idleActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName);
            int busyActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(0, idleActivationsNotCollected);
            Assert.Equal(busyGrainCount, busyActivationsNotCollected);

            quit[0] = true;
        }          
        
        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ManualCollectionShouldNotCollectBusyActivations()
        {
            await Initialize(DEFAULT_IDLE_TIMEOUT);

            TimeSpan shortIdleTimeout = TimeSpan.FromSeconds(1);
            const int idleGrainCount = 500;
            const int busyGrainCount = 500;
            var idleGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain1));
            var busyGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(BusyActivationGcTestGrain1));

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain1> busyGrains = new List<IBusyActivationGcTestGrain1>();
            logger.LogInformation("ManualCollectionShouldNotCollectBusyActivations: activating {Count} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);
            bool[] quit = new bool[]{ false };
            async Task busyWorker()
            {
                logger.LogInformation("ManualCollectionShouldNotCollectBusyActivations: busyWorker started");
                List<Task> tasks1 = new List<Task>();
                while (!quit[0])
                {
                    foreach (var g in busyGrains)
                        tasks1.Add(g.Nop());
                    await Task.WhenAll(tasks1);
                }
            }
            Task.Run(busyWorker).Ignore();

            logger.LogInformation("ManualCollectionShouldNotCollectBusyActivations: activating {Count} idle grains.", idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName) + await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(idleGrainCount + busyGrainCount, activationsCreated);

            logger.LogInformation(
                "ManualCollectionShouldNotCollectBusyActivations: grains activated; waiting {TotalSeconds} sec before triggering manual collection.",
                shortIdleTimeout.TotalSeconds);
            
            // Give the busyWorker time to start making requests on real time
            // This ensures all busy grains have recent activity before we advance virtual time
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            TimeSpan everything = TimeSpan.FromMinutes(10);
            
            // Advance virtual time so idle grains are considered idle for more than 10 minutes.
            _sharedTimeProvider!.Advance(everything + TimeSpan.FromSeconds(5));
            
            // Give the busy worker time to cycle through all grains after time advancement.
            // This ensures busy grains have their idle timestamp reset to the new (advanced) time,
            // so they won't be collected.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            
            logger.LogInformation("ManualCollectionShouldNotCollectBusyActivations: triggering manual collection (timespan is {TotalSeconds} sec).",  everything.TotalSeconds);
            IManagementGrain mgmtGrain = this.testCluster.GrainFactory.GetGrain<IManagementGrain>(0);
            await mgmtGrain.ForceActivationCollection(everything);

            logger.LogInformation(
                "ManualCollectionShouldNotCollectBusyActivations: waiting for {Count} idle grain deactivations (activation GC idle timeout is {DefaultIdleTime} sec).",
                idleGrainCount,
                DEFAULT_IDLE_TIMEOUT.TotalSeconds);

            // Wait for all idle grains to be deactivated using event-driven approach
            // Pass the FakeTimeProvider to continue advancing time during polling
            await _diagnosticObserver.WaitForDeactivationCountAsync("IdleActivationGcTestGrain1", idleGrainCount, MAX_WAIT_TIME, _sharedTimeProvider);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain).
            int idleActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName);
            int busyActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(0, idleActivationsNotCollected);
            Assert.Equal(busyGrainCount, busyActivationsNotCollected);

            quit[0] = true;
        }    
        
        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldCollectIdleActivationsSpecifiedInPerTypeConfiguration()
        {
            //make sure default value won't cause activation collection during wait time
            var defaultCollectionAgeLimit = MAX_WAIT_TIME.Multiply(2);
            await Initialize(defaultCollectionAgeLimit);

            const int grainCount = 1000;
            var fullGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain2));

            List<Task> tasks = new List<Task>();
            logger.LogInformation("ActivationCollectorShouldCollectIdleActivationsSpecifiedInPerTypeConfiguration: activating {Count} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain2 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain2>(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(grainCount, activationsCreated);

            logger.LogInformation(
                "ActivationCollectorShouldCollectIdleActivationsSpecifiedInPerTypeConfiguration: grains activated; waiting for {Count} deactivations (activation GC idle timeout is {DefaultIdleTime} sec).",
                grainCount,
                DEFAULT_IDLE_TIMEOUT.TotalSeconds);

            // Advance virtual time past the per-type collection age limit (DEFAULT_IDLE_TIMEOUT) so grains become stale.
            _sharedTimeProvider!.Advance(DEFAULT_IDLE_TIMEOUT + TimeSpan.FromSeconds(5));

            // Wait for all grains to be deactivated using event-driven approach
            // Pass the FakeTimeProvider to continue advancing time during polling
            await _diagnosticObserver.WaitForDeactivationCountAsync("IdleActivationGcTestGrain2", grainCount, MAX_WAIT_TIME, _sharedTimeProvider);

            int activationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(0, activationsNotCollected);
        }   

        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration()
        {
            //make sure default value won't cause activation collection during wait time
            var defaultCollectionAgeLimit = MAX_WAIT_TIME.Multiply(2);
            await Initialize(defaultCollectionAgeLimit);

            const int idleGrainCount = 500;
            const int busyGrainCount = 500;
            var idleGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain2));
            var busyGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(BusyActivationGcTestGrain2));

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain2> busyGrains = new List<IBusyActivationGcTestGrain2>();
            logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration: activating {Count} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain2 g = this.testCluster.GrainFactory.GetGrain<IBusyActivationGcTestGrain2>(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);
            bool[] quit = new bool[]{ false };
            async Task busyWorker()
            {
                logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration: busyWorker started");
                List<Task> tasks1 = new List<Task>();
                while (!quit[0])
                {
                    foreach (var g in busyGrains)
                        tasks1.Add(g.Nop());
                    await Task.WhenAll(tasks1);
                }
            }
            Task.Run(busyWorker).Ignore();

            logger.LogInformation("ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration: activating {Count} idle grains.", idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain2 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain2>(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName) + await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(idleGrainCount + busyGrainCount, activationsCreated);

            logger.LogInformation(
                "ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration: grains activated; waiting for {Count} idle grain deactivations (activation GC idle timeout is {DefaultIdleTime} sec).",
                idleGrainCount,
                DEFAULT_IDLE_TIMEOUT.TotalSeconds);

            // Advance virtual time past the per-type collection age limit (DEFAULT_IDLE_TIMEOUT) so idle grains become stale.
            // Busy grains will continue making real-time requests, keeping them active.
            _sharedTimeProvider!.Advance(DEFAULT_IDLE_TIMEOUT + TimeSpan.FromSeconds(5));

            // Wait for all idle grains to be deactivated using event-driven approach
            // Pass the FakeTimeProvider to continue advancing time during polling
            await _diagnosticObserver.WaitForDeactivationCountAsync("IdleActivationGcTestGrain2", idleGrainCount, MAX_WAIT_TIME, _sharedTimeProvider);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain2).
            int idleActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName);
            int busyActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(0, idleActivationsNotCollected);
            Assert.Equal(busyGrainCount, busyActivationsNotCollected);

            quit[0] = true;
        } 
  
        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldNotCollectBusyStatelessWorkers()
        {
            await Initialize(DEFAULT_IDLE_TIMEOUT);

            // The purpose of this test is to verify that idle stateless worker activations are properly
            // identified and collected by the activation collector, while busy activations are retained.
            //
            // Test steps:
            //   1. Create a stateless worker grain and send a burst of concurrent requests to create multiple activations
            //   2. Verify that multiple activations were created
            //   3. Start a background task that keeps one activation busy with periodic requests
            //   4. Wait for idle activations to be collected (using event-driven waiting)
            //   5. Verify that exactly one activation remains (the busy one)
            //   6. Repeat to ensure the behavior is consistent

            const int grainCount = 1;
            var grainTypeName = RuntimeTypeNameFormatter.Format(typeof(StatelessWorkerActivationCollectorTestGrain1));
            const int burstLength = 1000;

            List<IStatelessWorkerActivationCollectorTestGrain1> grains = new List<IStatelessWorkerActivationCollectorTestGrain1>();
            for (var i = 0; i < grainCount; ++i)
            {
                IStatelessWorkerActivationCollectorTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IStatelessWorkerActivationCollectorTestGrain1>(Guid.NewGuid());
                grains.Add(g);
            }

            using var workerCts = new CancellationTokenSource();

            async Task KeepGrainBusyAsync(IStatelessWorkerActivationCollectorTestGrain1 grain, CancellationToken ct)
            {
                // Keep one activation busy by sending periodic requests
                // The delay should be less than the idle timeout to prevent collection
                var keepAliveInterval = DEFAULT_IDLE_TIMEOUT.Divide(2);
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await grain.Delay(keepAliveInterval);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

            for (int iteration = 0; iteration < 2; ++iteration)
            {
                // Clear deactivation events from previous iteration
                _diagnosticObserver.Clear();

                // Step 1: Send a burst of concurrent requests to create multiple activations
                this.logger.LogInformation(
                    "ActivationCollectorShouldNotCollectBusyStatelessWorkers: activating stateless worker grains (iteration #{Iteration}).",
                    iteration);

                var burstTasks = new List<Task>();
                foreach (var g in grains)
                {
                    for (int j = 0; j < burstLength; ++j)
                    {
                        // The delay ensures one activation cannot serve all requests, forcing creation of additional activations
                        burstTasks.Add(g.Delay(TimeSpan.FromMilliseconds(10)));
                    }
                }
                await Task.WhenAll(burstTasks);

                // Step 2: Verify that multiple activations were created
                int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, grainTypeName);
                Assert.True(activationsCreated > grainCount,
                    $"Expected more than {grainCount} activations to be created; got {activationsCreated} instead");

                this.logger.LogInformation(
                    "ActivationCollectorShouldNotCollectBusyStatelessWorkers: {ActivationsCreated} activations created.",
                    activationsCreated);

                // Calculate expected deactivations: all activations except one per grain should be collected
                int expectedDeactivations = activationsCreated - grainCount;

                // Step 3: Start background task to keep one activation busy
                using var iterationCts = new CancellationTokenSource();
                var busyTasks = grains.Select(g => KeepGrainBusyAsync(g, iterationCts.Token)).ToList();

                // Step 4: Advance virtual time past the collection age limit so idle activations become stale.
                // The busy activation will continue making requests on real time, but idle activations
                // will have their GetIdleness() return a value exceeding DEFAULT_IDLE_TIMEOUT (11 seconds).
                _sharedTimeProvider!.Advance(DEFAULT_IDLE_TIMEOUT + TimeSpan.FromSeconds(5));

                // Wait for idle activations to be collected using event-driven approach
                this.logger.LogInformation(
                    "ActivationCollectorShouldNotCollectBusyStatelessWorkers: waiting for {ExpectedDeactivations} deactivations.",
                    expectedDeactivations);

                // Pass the FakeTimeProvider to continue advancing time during polling
                await _diagnosticObserver.WaitForDeactivationCountAsync(
                    "StatelessWorkerActivationCollectorTestGrain1",
                    expectedDeactivations,
                    MAX_WAIT_TIME,
                    _sharedTimeProvider);

                // Step 5: Stop the busy worker and verify exactly one activation remains
                iterationCts.Cancel();
                try
                {
                    await Task.WhenAll(busyTasks);
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                }

                int remainingActivations = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, grainTypeName);
                Assert.Equal(grainCount, remainingActivations);

                this.logger.LogInformation(
                    "ActivationCollectorShouldNotCollectBusyStatelessWorkers: iteration {Iteration} completed successfully. {Remaining} activation(s) remaining.",
                    iteration,
                    remainingActivations);
            }
        }

        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldCollectByCollectionSpecificAgeLimitForTwelveSeconds()
        {
            var defaultCollectionAge = MAX_WAIT_TIME.Multiply(2);
            //make sure defaultCollectionAge value won't cause activation collection in wait time
            await Initialize(defaultCollectionAge);

            const int grainCount = 1000;

            // CollectionAgeLimit = 12 seconds for this grain type
            var fullGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain));

            List<Task> tasks = new List<Task>();
            logger.LogInformation("ActivationCollectorShouldCollectByCollectionSpecificAgeLimit: activating {GrainCount} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain g = this.testCluster.GrainFactory.GetGrain<ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain>(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(grainCount, activationsCreated);

            logger.LogInformation(
                "ActivationCollectorShouldCollectByCollectionSpecificAgeLimit: grains activated; waiting for {Count} deactivations (collection age limit is 12 sec).",
                grainCount);

            // Advance virtual time past the collection age limit (12 seconds) so grains become stale.
            // The activation collector timer runs on real time, but GetIdleness() uses TimeProvider.
            // We advance by 15 seconds to ensure grains are considered idle for longer than the 12-second limit.
            _sharedTimeProvider!.Advance(TimeSpan.FromSeconds(15));

            // Wait for all grains to be deactivated using event-driven approach
            // Pass the FakeTimeProvider to continue advancing time during polling
            await _diagnosticObserver.WaitForDeactivationCountAsync("CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain", grainCount, MAX_WAIT_TIME, _sharedTimeProvider);

            int activationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(0, activationsNotCollected);
        }

        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task NonReentrantGrainTimer_NoKeepAlive_Test()
        {
            await Initialize(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));

            const string testName = "NonReentrantGrainTimer_NoKeepAlive_Test";

            var grain = this.testCluster.GrainFactory.GetGrain<INonReentrantTimerCallGrain>(GetRandomGrainId());

            // Schedule a timer to fire after 10 seconds, which will not extend the grain's lifetime.
            // Since the collection age is 5 seconds and keepAlive is false, the grain should be
            // collected before the timer fires.
            await grain.StartTimer(testName, TimeSpan.FromSeconds(10), keepAlive: false);

            // Advance virtual time past the collection age (5 seconds) so the grain becomes stale.
            // We advance by 6 seconds to ensure the grain is considered idle for longer than the 5-second limit.
            // This should trigger collection before the timer fires (which was scheduled for 10 seconds).
            _sharedTimeProvider!.Advance(TimeSpan.FromSeconds(6));

            // Wait for the grain to be deactivated using event-driven approach
            logger.LogInformation("NonReentrantGrainTimer_NoKeepAlive_Test: waiting for grain deactivation.");
            await _diagnosticObserver.WaitForDeactivationCountAsync("NonReentrantTimerCallGrain", 1, TimeSpan.FromSeconds(30));

            var tickCount = await grain.GetTickCount();

            // The grain should have been deactivated before the timer fired.
            Assert.Equal(0, tickCount);
        }

    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Serialization.TypeSystem;
using Orleans.TestingHost;
using Tester;
using TestExtensions;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;

namespace UnitTests.ActivationsLifeCycleTests
{
    public class ActivationCollectorTests : OrleansTestingBase, IAsyncLifetime
    {
        private static readonly TimeSpan DEFAULT_COLLECTION_QUANTUM = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DEFAULT_IDLE_TIMEOUT = DEFAULT_COLLECTION_QUANTUM + TimeSpan.FromSeconds(1);
        private static readonly TimeSpan WAIT_TIME = DEFAULT_IDLE_TIMEOUT.Multiply(3.0);

        private TestCluster testCluster;

        private ILogger logger;

        private async Task Initialize(TimeSpan collectionAgeLimit, TimeSpan quantum)
        {
            var builder = new TestClusterBuilder(1);
            builder.Properties["CollectionQuantum"] = quantum.ToString();
            builder.Properties["DefaultCollectionAgeLimit"] = collectionAgeLimit.ToString();
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            testCluster = builder.Build();
            await testCluster.DeployAsync();
            this.logger = this.testCluster.Client.ServiceProvider.GetRequiredService<ILogger<ActivationCollectorTests>>();
        }

        public class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                var config = hostBuilder.GetConfiguration();
                var collectionAgeLimit = TimeSpan.Parse(config["DefaultCollectionAgeLimit"]);
                var quantum = TimeSpan.Parse(config["CollectionQuantum"]);
                hostBuilder
                    .ConfigureDefaults()
                    .ConfigureServices(services => services.Where(s => s.ServiceType == typeof(IConfigurationValidator)).ToList().ForEach(s => services.Remove(s)));
                hostBuilder.Configure<GrainCollectionOptions>(options =>
                {
                    options.CollectionAge = collectionAgeLimit;
                    options.CollectionQuantum = quantum;
                    options.ClassSpecificCollectionAge = new Dictionary<string, TimeSpan>
                    {
                        [typeof(IdleActivationGcTestGrain2).FullName] = DEFAULT_IDLE_TIMEOUT,
                        [typeof(BusyActivationGcTestGrain2).FullName] = DEFAULT_IDLE_TIMEOUT,
                        [typeof(CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain).FullName] = TimeSpan.FromSeconds(12),
                    };
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
            logger.Info("ActivationCollectorForceCollection: activating {0} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            await Task.Delay(TimeSpan.FromSeconds(5));

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
            logger.Info("IdleActivationCollectorShouldCollectIdleActivations: activating {0} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(grainCount, activationsCreated);

            logger.Info("IdleActivationCollectorShouldCollectIdleActivations: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

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
            logger.Info("ActivationCollectorShouldNotCollectBusyActivations: activating {0} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);
            bool[] quit = new bool[]{ false };
            Func<Task> busyWorker =
                async () =>
                {
                    logger.Info("ActivationCollectorShouldNotCollectBusyActivations: busyWorker started");
                    List<Task> tasks1 = new List<Task>();
                    while (!quit[0])
                    {
                        foreach (var g in busyGrains)
                            tasks1.Add(g.Nop());
                        await Task.WhenAll(tasks1);
                    }
                };
            Task.Run(busyWorker).Ignore();

            logger.Info("ActivationCollectorShouldNotCollectBusyActivations: activating {0} idle grains.", idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName) + await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(idleGrainCount + busyGrainCount, activationsCreated);

            logger.Info("ActivationCollectorShouldNotCollectBusyActivations: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

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
            logger.Info("ManualCollectionShouldNotCollectBusyActivations: activating {0} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);
            bool[] quit = new bool[]{ false };
            Func<Task> busyWorker =
                async () =>
                {
                    logger.Info("ManualCollectionShouldNotCollectBusyActivations: busyWorker started");
                    List<Task> tasks1 = new List<Task>();
                    while (!quit[0])
                    {
                        foreach (var g in busyGrains)
                            tasks1.Add(g.Nop());
                        await Task.WhenAll(tasks1);
                    }
                };
            Task.Run(busyWorker).Ignore();

            logger.Info("ManualCollectionShouldNotCollectBusyActivations: activating {0} idle grains.", idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName) + await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(idleGrainCount + busyGrainCount, activationsCreated);

            logger.Info("ManualCollectionShouldNotCollectBusyActivations: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", shortIdleTimeout.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(shortIdleTimeout);

            TimeSpan everything = TimeSpan.FromMinutes(10);
            logger.Info("ManualCollectionShouldNotCollectBusyActivations: triggering manual collection (timespan is {0} sec).",  everything.TotalSeconds);
            IManagementGrain mgmtGrain = this.testCluster.GrainFactory.GetGrain<IManagementGrain>(0);
            await mgmtGrain.ForceActivationCollection(everything);
            

            logger.Info("ManualCollectionShouldNotCollectBusyActivations: waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

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
            var defaultCollectionAgeLimit = WAIT_TIME.Multiply(2);
            await Initialize(defaultCollectionAgeLimit);

            const int grainCount = 1000;
            var fullGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain2));

            List<Task> tasks = new List<Task>();
            logger.Info("ActivationCollectorShouldCollectIdleActivationsSpecifiedInPerTypeConfiguration: activating {0} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain2 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain2>(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(grainCount, activationsCreated);

            logger.Info("ActivationCollectorShouldCollectIdleActivationsSpecifiedInPerTypeConfiguration: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            int activationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(0, activationsNotCollected);
        }   

        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration()
        {
            //make sure default value won't cause activation collection during wait time
            var defaultCollectionAgeLimit = WAIT_TIME.Multiply(2);
            await Initialize(defaultCollectionAgeLimit);

            const int idleGrainCount = 500;
            const int busyGrainCount = 500;
            var idleGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain2));
            var busyGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(BusyActivationGcTestGrain2));

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain2> busyGrains = new List<IBusyActivationGcTestGrain2>();
            logger.Info("ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration: activating {0} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain2 g = this.testCluster.GrainFactory.GetGrain<IBusyActivationGcTestGrain2>(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);
            bool[] quit = new bool[]{ false };
            Func<Task> busyWorker =
                async () =>
                {
                    logger.Info("ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration: busyWorker started");
                    List<Task> tasks1 = new List<Task>();
                    while (!quit[0])
                    {
                        foreach (var g in busyGrains)
                            tasks1.Add(g.Nop());
                        await Task.WhenAll(tasks1);
                    }
                };
            Task.Run(busyWorker).Ignore();

            logger.Info("ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration: activating {0} idle grains.", idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain2 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain2>(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName) + await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(idleGrainCount + busyGrainCount, activationsCreated);

            logger.Info("IdleActivationCollectorShouldNotCollectBusyActivations: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain2).
            int idleActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName);
            int busyActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(0, idleActivationsNotCollected);
            Assert.Equal(busyGrainCount, busyActivationsNotCollected);

            quit[0] = true;
        } 
  
        [Fact(Skip = "Flaky test. Needs to be investigated."), TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldNotCollectBusyStatelessWorkers()
        {
            await Initialize(DEFAULT_IDLE_TIMEOUT);

            // the purpose of this test is to determine whether idle stateless worker activations are properly identified by the activation collector.
            // in this test, we:
            //
            //   1. setup the test.
            //   2. activate a set of grains by sending a burst of messages to each one. the purpose of the burst is to ensure that multiple activations are used. 
            //   3. verify that multiple activations for each grain have been created.
            //   4. periodically send a message to each grain, ensuring that only one activation remains busy. each time we check the activation id and compare it against the activation id returned by the previous grain call. initially, these may not be identical but as the other activations become idle and are collected, there will be only one activation servicing these calls.
            //   5. wait long enough for idle activations to be collected.
            //   6. verify that only one activation is still active per grain.
            //   7. ensure that test steps 2-6 are repeatable.

            const int grainCount = 1;
            var grainTypeName = RuntimeTypeNameFormatter.Format(typeof(StatelessWorkerActivationCollectorTestGrain1));
            const int burstLength = 1000;

            List<Task> tasks0 = new List<Task>();
            List<IStatelessWorkerActivationCollectorTestGrain1> grains = new List<IStatelessWorkerActivationCollectorTestGrain1>();
            for (var i = 0; i < grainCount; ++i)
            {
                IStatelessWorkerActivationCollectorTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IStatelessWorkerActivationCollectorTestGrain1>(Guid.NewGuid());
                grains.Add(g);
            }


            bool[] quit = new bool[] { false };
            bool[] matched = new bool[grainCount];
            string[] activationIds = new string[grainCount];
            Func<int, Task> workFunc =
                async index =>
                {
                    // (part of) 4. periodically send a message to each grain...

                    // take a grain and call Delay to keep it busy.
                    IStatelessWorkerActivationCollectorTestGrain1 g = grains[index];
                    await g.Delay(DEFAULT_IDLE_TIMEOUT.Divide(2));
                    // identify the activation and record whether it matches the activation ID last reported. it probably won't match in the beginning but should always converge on a match as other activations get collected.
                    string aid = await g.IdentifyActivation();
                    logger.Info("ActivationCollectorShouldNotCollectBusyStatelessWorkers: identified {0}", aid);
                    matched[index] = aid == activationIds[index];
                    activationIds[index] = aid;
                };
            Func<Task> workerFunc =
                async () =>
                {
                    // (part of) 4. periodically send a message to each grain...
                    logger.Info("ActivationCollectorShouldNotCollectBusyStatelessWorkers: busyWorker started");

                    List<Task> tasks1 = new List<Task>();
                    while (!quit[0])
                    {
                        for (int index = 0; index < grains.Count; ++index)
                        {
                            if (quit[0])
                            {
                                break;
                            }

                            tasks1.Add(workFunc(index));
                        }
                        await Task.WhenAll(tasks1);
                    }
                };

            // setup (1) ends here.

            for (int i = 0; i < 2; ++i)
            {
                // 2. activate a set of grains... 
                this.logger.Info("ActivationCollectorShouldNotCollectBusyStatelessWorkers: activating {0} stateless worker grains (run #{1}).", grainCount, i);
                foreach (var g in grains)
                {
                    for (int j = 0; j < burstLength; ++j)
                    {
                        // having the activation delay will ensure that one activation cannot serve all requests that we send to it, making it so that additional activations will be created.
                        tasks0.Add(g.Delay(TimeSpan.FromMilliseconds(10)));
                    }
                }
                await Task.WhenAll(tasks0);


                // 3. verify that multiple activations for each grain have been created.
                int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, grainTypeName);
                Assert.True(activationsCreated > grainCount, string.Format("more than {0} activations should have been created; got {1} instead", grainCount, activationsCreated));

                // 4. periodically send a message to each grain...
                this.logger.Info("ActivationCollectorShouldNotCollectBusyStatelessWorkers: grains activated; sending heartbeat to {0} stateless worker grains.", grainCount);
                Task workerTask = Task.Run(workerFunc);

                // 5. wait long enough for idle activations to be collected.
                this.logger.Info("ActivationCollectorShouldNotCollectBusyStatelessWorkers: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
                await Task.Delay(WAIT_TIME);

                // 6. verify that only one activation is still active per grain.
                int busyActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, grainTypeName);

                // signal that the worker task should stop and wait for it to finish.
                quit[0] = true;
                await workerTask;
                quit[0] = false;

                Assert.Equal(grainCount, busyActivationsNotCollected);

                // verify that we matched activation ids in the final iteration of step 4's loop.
                for (int index = 0; index < grains.Count; ++index)
                {
                    Assert.True(matched[index], string.Format("activation ID of final subsequent heartbeats did not match for grain {0}", grains[index]));
                }
            }
        }

        [Fact, TestCategory("ActivationCollector"), TestCategory("Performance"), TestCategory("CorePerf")]
        public async Task ActivationCollectorShouldNotCauseMessageLoss()
        {
            await Initialize(DEFAULT_IDLE_TIMEOUT);

            const int idleGrainCount = 0;
            const int busyGrainCount = 500;
            var idleGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(IdleActivationGcTestGrain1));
            var busyGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(BusyActivationGcTestGrain1));
            const int burstCount = 100;

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain1> busyGrains = new List<IBusyActivationGcTestGrain1>();
            logger.Info("ActivationCollectorShouldNotCauseMessageLoss: activating {0} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IBusyActivationGcTestGrain1>(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }

            await busyGrains[0].EnableBurstOnCollection(burstCount);

            logger.Info("ActivationCollectorShouldNotCauseMessageLoss: activating {0} idle grains.", idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = this.testCluster.GrainFactory.GetGrain<IIdleActivationGcTestGrain1>(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName) + await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(idleGrainCount + busyGrainCount, activationsCreated);

            logger.Info("ActivationCollectorShouldNotCauseMessageLoss: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain1).
            int idleActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, idleGrainTypeName);
            int busyActivationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, busyGrainTypeName);
            Assert.Equal(0, idleActivationsNotCollected);
            Assert.Equal(busyGrainCount, busyActivationsNotCollected);
        }

        [Fact, TestCategory("ActivationCollector"), TestCategory("Functional")]
        public async Task ActivationCollectorShouldCollectByCollectionSpecificAgeLimitForTwelveSeconds()
        {
            var waitTime = TimeSpan.FromSeconds(30);
            var defaultCollectionAge = waitTime.Multiply(2);
            //make sure defaultCollectionAge value won't cause activation collection in wait time
            await Initialize(defaultCollectionAge);

            const int grainCount = 1000;

            // CollectionAgeLimit = 12 seconds
            var fullGrainTypeName = RuntimeTypeNameFormatter.Format(typeof(CollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain));

            List<Task> tasks = new List<Task>();
            logger.Info("ActivationCollectorShouldCollectByCollectionSpecificAgeLimit: activating {0} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain g = this.testCluster.GrainFactory.GetGrain<ICollectionSpecificAgeLimitForTenSecondsActivationGcTestGrain>(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(grainCount, activationsCreated);

            logger.Info("ActivationCollectorShouldCollectByCollectionSpecificAgeLimit: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            
            // Some time is required for GC to collect all of the Grains)
            await Task.Delay(waitTime);

            int activationsNotCollected = await TestUtils.GetActivationCount(this.testCluster.GrainFactory, fullGrainTypeName);
            Assert.Equal(0, activationsNotCollected);
        }
    }
}

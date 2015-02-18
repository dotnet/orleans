using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using UnitTestGrainInterfaces;

namespace UnitTests.General
{
    [DeploymentItem("Config_ActivationCollectorTests.xml")]
    [TestClass]
    public class ActivationCollectorTests : UnitTestBase
    {
        static private readonly TimeSpan DEFAULT_COLLECTION_QUANTUM = TimeSpan.FromSeconds(10);
        static private readonly TimeSpan DEFAULT_IDLE_TIMEOUT = DEFAULT_COLLECTION_QUANTUM;
        static private readonly TimeSpan WAIT_TIME = DEFAULT_IDLE_TIMEOUT.Multiply(3.0);

        [ClassCleanup]
        public static void ClassCleanup()
        {
            ResetDefaultRuntimes();
        }

        private void Initialize(TimeSpan collectionAgeLimit, TimeSpan quantum)
        {
            ResetDefaultRuntimes();
            UnitTestBase.Initialize(new Options { SiloConfigFile = new FileInfo("Config_ActivationCollectorTests.xml"), StartFreshOrleans = true, StartPrimary = true, StartSecondary = false, DefaultCollectionAgeLimit = collectionAgeLimit, CollectionQuantum = quantum});
        }

        private void Initialize(TimeSpan collectionAgeLimit)
        {
            Initialize(collectionAgeLimit, collectionAgeLimit);
        }

        private void Initialize()
        {
            Initialize(TimeSpan.Zero, DEFAULT_COLLECTION_QUANTUM);
        }

        [TestMethod, TestCategory("ActivationCollector"), TestCategory("Nightly")]
        public async Task ActivationCollectorShouldCollectIdleActivations()
        {
            Initialize(DEFAULT_IDLE_TIMEOUT);

            const int grainCount = 1000;
            const string fullGrainTypeName = "UnitTestGrains.IdleActivationGcTestGrain1";

            List<Task> tasks = new List<Task>();
            logger.Info("IdleActivationCollectorShouldCollectIdleActivations: activating {0} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = IdleActivationGcTestGrain1Factory.GetGrain(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await GetActivationCount(fullGrainTypeName);
            Assert.AreEqual(grainCount, activationsCreated, string.Format("{0} activations should have been created; got {1} instead", grainCount, activationsCreated));

            logger.Info("IdleActivationCollectorShouldCollectIdleActivations: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            int activationsNotCollected = await GetActivationCount(fullGrainTypeName);
            Assert.AreEqual(0, activationsNotCollected, string.Format("{0} activations should have been collected", activationsNotCollected));
        }   

        [TestMethod, TestCategory("ActivationCollector"), TestCategory("Nightly")]
        public async Task ActivationCollectorShouldNotCollectBusyActivations()
        {
            Initialize(DEFAULT_IDLE_TIMEOUT);

            const int idleGrainCount = 500;
            const int busyGrainCount = 500;
            const string idleGrainTypeName = "UnitTestGrains.IdleActivationGcTestGrain1";
            const string busyGrainTypeName = "UnitTestGrains.BusyActivationGcTestGrain1";

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain1> busyGrains = new List<IBusyActivationGcTestGrain1>();
            logger.Info("ActivationCollectorShouldNotCollectBusyActivations: activating {0} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g = BusyActivationGcTestGrain1Factory.GetGrain(Guid.NewGuid());
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
                IIdleActivationGcTestGrain1 g = IdleActivationGcTestGrain1Factory.GetGrain(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await GetActivationCount(idleGrainTypeName) + await GetActivationCount(busyGrainTypeName);
            Assert.AreEqual(idleGrainCount + busyGrainCount, activationsCreated, string.Format("{0} activations should have been created; got {1} instead", idleGrainCount + busyGrainCount, activationsCreated));

            logger.Info("ActivationCollectorShouldNotCollectBusyActivations: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain1).
            int idleActivationsNotCollected = await GetActivationCount(idleGrainTypeName);
            int busyActivationsNotCollected = await GetActivationCount(busyGrainTypeName);
            Assert.AreEqual(0, idleActivationsNotCollected, string.Format("{0} idle activations should have been collected", idleActivationsNotCollected));
            Assert.AreEqual(busyGrainCount, busyActivationsNotCollected, string.Format("{0} busy activations should not have been collected", busyActivationsNotCollected));

            quit[0] = true;
        }          
        
        [TestMethod, TestCategory("ActivationCollector"), TestCategory("Nightly")]
        public async Task ManualCollectionShouldNotCollectBusyActivations()
        {
            Initialize(DEFAULT_IDLE_TIMEOUT);

            TimeSpan shortIdleTimeout = TimeSpan.FromSeconds(1);
            const int idleGrainCount = 500;
            const int busyGrainCount = 500;
            const string idleGrainTypeName = "UnitTestGrains.IdleActivationGcTestGrain1";
            const string busyGrainTypeName = "UnitTestGrains.BusyActivationGcTestGrain1";

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain1> busyGrains = new List<IBusyActivationGcTestGrain1>();
            logger.Info("ManualCollectionShouldNotCollectBusyActivations: activating {0} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g = BusyActivationGcTestGrain1Factory.GetGrain(Guid.NewGuid());
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
                IIdleActivationGcTestGrain1 g = IdleActivationGcTestGrain1Factory.GetGrain(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await GetActivationCount(idleGrainTypeName) + await GetActivationCount(busyGrainTypeName);
            Assert.AreEqual(idleGrainCount + busyGrainCount, activationsCreated, string.Format("{0} activations should have been created; got {1} instead", idleGrainCount + busyGrainCount, activationsCreated));

            logger.Info("ManualCollectionShouldNotCollectBusyActivations: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", shortIdleTimeout.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(shortIdleTimeout);

            TimeSpan everything = TimeSpan.FromMinutes(10);
            logger.Info("ManualCollectionShouldNotCollectBusyActivations: triggering manual collection (timespan is {0} sec).",  everything.TotalSeconds);
            IManagementGrain mgmtGrain = ManagementGrainFactory.GetGrain(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
            await mgmtGrain.ForceActivationCollection(everything);
            

            logger.Info("ManualCollectionShouldNotCollectBusyActivations: waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain).
            int idleActivationsNotCollected = await GetActivationCount(idleGrainTypeName);
            int busyActivationsNotCollected = await GetActivationCount(busyGrainTypeName);
            Assert.AreEqual(0, idleActivationsNotCollected, string.Format("{0} idle activations should have been collected", idleActivationsNotCollected));
            Assert.AreEqual(busyGrainCount, busyActivationsNotCollected, string.Format("{0} busy activations should not have been collected", busyActivationsNotCollected));

            quit[0] = true;
        }    
        
        [TestMethod, TestCategory("ActivationCollector"), TestCategory("Nightly")]
        public async Task ActivationCollectorShouldNotCollectIdleActivationsIfDisabled()
        {
            Initialize();

            const int grainCount = 1000;
            const string fullGrainTypeName = "UnitTestGrains.IdleActivationGcTestGrain1";

            List<Task> tasks = new List<Task>();
            logger.Info("ActivationCollectorShouldNotCollectIdleActivationsIfDisabled: activating {0} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = IdleActivationGcTestGrain1Factory.GetGrain(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await GetActivationCount(fullGrainTypeName);
            Assert.AreEqual(grainCount, activationsCreated, string.Format("{0} activations should have been created; got {1} instead", grainCount, activationsCreated));

            logger.Info("ActivationCollectorShouldNotCollectIdleActivationsIfDisabled: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            int activationsNotCollected = await GetActivationCount(fullGrainTypeName);
            Assert.AreEqual(1000, activationsNotCollected, "0 activations should have been collected");
        }   
        
        [TestMethod, TestCategory("ActivationCollector"), TestCategory("Nightly")]
        public async Task ActivationCollectorShouldCollectIdleActivationsSpecifiedInPerTypeConfiguration()
        {
            Initialize();

            const int grainCount = 1000;
            const string fullGrainTypeName = "UnitTestGrains.IdleActivationGcTestGrain2";

            List<Task> tasks = new List<Task>();
            logger.Info("ActivationCollectorShouldCollectIdleActivationsSpecifiedInPerTypeConfiguration: activating {0} grains.", grainCount);
            for (var i = 0; i < grainCount; ++i)
            {
                IIdleActivationGcTestGrain2 g = IdleActivationGcTestGrain2Factory.GetGrain(Guid.NewGuid());
                tasks.Add(g.Nop());
            }
            await Task.WhenAll(tasks);

            int activationsCreated = await GetActivationCount(fullGrainTypeName);
            Assert.AreEqual(grainCount, activationsCreated, string.Format("{0} activations should have been created; got {1} instead", grainCount, activationsCreated));

            logger.Info("ActivationCollectorShouldCollectIdleActivationsSpecifiedInPerTypeConfiguration: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            int activationsNotCollected = await GetActivationCount(fullGrainTypeName);
            Assert.AreEqual(0, activationsNotCollected, string.Format("{0} activations should have been collected", activationsNotCollected));
        }   

        [TestMethod, TestCategory("ActivationCollector"), TestCategory("Nightly")]
        public async Task ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration()
        {
            Initialize();

            const int idleGrainCount = 500;
            const int busyGrainCount = 500;
            const string idleGrainTypeName = "UnitTestGrains.IdleActivationGcTestGrain2";
            const string busyGrainTypeName = "UnitTestGrains.BusyActivationGcTestGrain2";

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain2> busyGrains = new List<IBusyActivationGcTestGrain2>();
            logger.Info("ActivationCollectorShouldNotCollectBusyActivationsSpecifiedInPerTypeConfiguration: activating {0} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain2 g = BusyActivationGcTestGrain2Factory.GetGrain(Guid.NewGuid());
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
                IIdleActivationGcTestGrain2 g = IdleActivationGcTestGrain2Factory.GetGrain(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await GetActivationCount(idleGrainTypeName) + await GetActivationCount(busyGrainTypeName);
            Assert.AreEqual(idleGrainCount + busyGrainCount, activationsCreated, string.Format("{0} activations should have been created; got {1} instead", idleGrainCount + busyGrainCount, activationsCreated));

            logger.Info("IdleActivationCollectorShouldNotCollectBusyActivations: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain2).
            int idleActivationsNotCollected = await GetActivationCount(idleGrainTypeName);
            int busyActivationsNotCollected = await GetActivationCount(busyGrainTypeName);
            Assert.AreEqual(0, idleActivationsNotCollected, string.Format("{0} idle activations should have been collected", idleActivationsNotCollected));
            Assert.AreEqual(busyGrainCount, busyActivationsNotCollected, string.Format("{0} busy activations should not have been collected", busyActivationsNotCollected));

            quit[0] = true;
        } 
  
        [TestMethod, TestCategory("ActivationCollector"), TestCategory("Nightly")]
        public async Task ActivationCollectorShouldNotCollectBusyStatelessWorkers()
        {
            Initialize(DEFAULT_IDLE_TIMEOUT);

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
            const string grainTypeName = "UnitTestGrains.StatelessWorkerActivationCollectorTestGrain1";
            const int burstLength = 1000;

            List<Task> tasks0 = new List<Task>();
            List<IStatelessWorkerActivationCollectorTestGrain1> grains = new List<IStatelessWorkerActivationCollectorTestGrain1>();
            for (var i = 0; i < grainCount; ++i)
            {
                IStatelessWorkerActivationCollectorTestGrain1 g = StatelessWorkerActivationCollectorTestGrain1Factory.GetGrain(Guid.NewGuid());
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
                logger.Info("ActivationCollectorShouldNotCollectBusyStatelessWorkers: activating {0} stateless worker grains (run #{1}).", grainCount, i);
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
                int activationsCreated = await GetActivationCount(grainTypeName);
                Assert.IsTrue(activationsCreated > grainCount, string.Format("more than {0} activations should have been created; got {1} instead", grainCount, activationsCreated));

                // 4. periodically send a message to each grain...
                logger.Info("ActivationCollectorShouldNotCollectBusyStatelessWorkers: grains activated; sending heartbeat to {0} stateless worker grains.", grainCount);
                Task workerTask = Task.Run(workerFunc);

                // 5. wait long enough for idle activations to be collected.
                logger.Info("ActivationCollectorShouldNotCollectBusyStatelessWorkers: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
                await Task.Delay(WAIT_TIME);

                // 6. verify that only one activation is still active per grain.
                int busyActivationsNotCollected = await GetActivationCount(grainTypeName);

                // signal that the worker task should stop and wait for it to finish.
                quit[0] = true;
                await workerTask;
                quit[0] = false;

                Assert.AreEqual(grainCount, busyActivationsNotCollected, string.Format("{0} busy activations should not have been collected", busyActivationsNotCollected));

                // verify that we matched activation ids in the final iteration of step 4's loop.
                for (int index = 0; index < grains.Count; ++index)
                {
                    Assert.IsTrue(matched[index], "activation ID of final subsequent heartbeats did not match for grain {0}", grains[index]);
                }
            }
        }

        [TestMethod, TestCategory("ActivationCollector"), TestCategory("Performance"), TestCategory("CorePerf")]
        public async Task ActivationCollectorShouldNotCauseMessageLoss()
        {
            Initialize(DEFAULT_IDLE_TIMEOUT);

            const int idleGrainCount = 0;
            const int busyGrainCount = 500;
            const string idleGrainTypeName = "UnitTestGrains.IdleActivationGcTestGrain1";
            const string busyGrainTypeName = "UnitTestGrains.BusyActivationGcTestGrain1";
            const int burstCount = 100;

            List<Task> tasks0 = new List<Task>();
            List<IBusyActivationGcTestGrain1> busyGrains = new List<IBusyActivationGcTestGrain1>();
            logger.Info("ActivationCollectorShouldNotCauseMessageLoss: activating {0} busy grains.", busyGrainCount);
            for (var i = 0; i < busyGrainCount; ++i)
            {
                IBusyActivationGcTestGrain1 g = BusyActivationGcTestGrain1Factory.GetGrain(Guid.NewGuid());
                busyGrains.Add(g);
                tasks0.Add(g.Nop());
            }

            await busyGrains[0].EnableBurstOnCollection(burstCount);

            logger.Info("ActivationCollectorShouldNotCauseMessageLoss: activating {0} idle grains.", idleGrainCount);
            tasks0.Clear();
            for (var i = 0; i < idleGrainCount; ++i)
            {
                IIdleActivationGcTestGrain1 g = IdleActivationGcTestGrain1Factory.GetGrain(Guid.NewGuid());
                tasks0.Add(g.Nop());
            }
            await Task.WhenAll(tasks0);

            int activationsCreated = await GetActivationCount(idleGrainTypeName) + await GetActivationCount(busyGrainTypeName);
            Assert.AreEqual(idleGrainCount + busyGrainCount, activationsCreated, string.Format("{0} activations should have been created; got {1} instead", idleGrainCount + busyGrainCount, activationsCreated));

            logger.Info("ActivationCollectorShouldNotCauseMessageLoss: grains activated; waiting {0} sec (activation GC idle timeout is {1} sec).", WAIT_TIME.TotalSeconds, DEFAULT_IDLE_TIMEOUT.TotalSeconds);
            await Task.Delay(WAIT_TIME);

            // we should have only collected grains from the idle category (IdleActivationGcTestGrain1).
            int idleActivationsNotCollected = await GetActivationCount(idleGrainTypeName);
            int busyActivationsNotCollected = await GetActivationCount(busyGrainTypeName);
            Assert.AreEqual(0, idleActivationsNotCollected, string.Format("{0} idle activations should have been collected", idleActivationsNotCollected));
            Assert.AreEqual(busyGrainCount, busyActivationsNotCollected, string.Format("{0} busy activations should not have been collected", busyActivationsNotCollected));
        }


#if DEBUG || REVISIT

        private static readonly Options CollectionTestOptions = new Options
        {
            StartFreshOrleans = true,
            StartSecondary = false,
            DefaultCollectionAgeLimit = TimeSpan.Zero,
        };

        [TestMethod, TestCategory("ActivationCollector")]
        public async Task CollectionTestAge()
        {
            var options = CollectionTestOptions.Copy();
            options.DefaultCollectionAgeLimit = TimeSpan.FromSeconds(2);
            await CollectionTestRun(options);
        }

        [TestMethod, TestCategory("ActivationCollector")]
        public async Task CollectionTestCount()
        {
            await CollectionTestRun(CollectionTestOptions.Copy());
        }

        private static async Task CollectionTestRun(Options options, Action after = null)
        {
            ResetDefaultRuntimes();
            UnitTestBase.Initialize(options);

            // create grains every 100 ms
            var grains = new List<ICollectionTestGrain>();
            var done = new List<Task>();
            for (var i = 0; i < 40; i++)
            {
                // touch grain and add to list
                var grain = CollectionTestGrainFactory.GetGrain(i);
                done.Add(grain.GetAge());
                grains.Add(grain);

                // keep retouching oldest
                if (i >= 10)
                    done.Add(grains[i % 10].GetAge());

                Thread.Sleep(100);
            }
            if (after != null)
                after();
            await Task.WhenAll(done);
            const int t = 100;

            List<int> ages = grains.Select(g => g.GetAge()).ToList()
                .Select(v => (int)v.Result.TotalMilliseconds).ToList();

            Assert.IsTrue(ages.Skip(11).Take(8).All(a => a < t),
                "Almost-oldest grains should have been collected & reactivated: " + ages.ToStrings());
            Assert.IsTrue(ages.Skip(32).All(a => a >= t),
                "Newest grains should not have been collected: " + ages.ToStrings());
            Assert.IsTrue(ages.Skip(1).Take(8).All(a => a >= t),
                "Oldest but busy grains should not have been collected: " + ages.ToStrings());
        }
#endif
    }
}

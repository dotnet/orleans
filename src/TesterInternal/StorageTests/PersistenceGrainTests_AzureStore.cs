//#define REREAD_STATE_AFTER_WRITE_FAILED

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace UnitTests.StorageTests
{
    /// <summary>
    /// PersistenceGrainTests using AzureStore - Requires access to external Azure storage
    /// </summary>
    [TestClass]
    [DeploymentItem("Config_AzureTableStorage.xml")]
    public class PersistenceGrainTests_AzureStore : UnitTestSiloHost
    {
        private readonly double timingFactor;

        private const int LoopIterations_Grain = 1000;
        private const int BatchSize = 100;

        private const int MaxReadTime = 200;
        private const int MaxWriteTime = 2000;

        private static readonly Guid initialServiceId = Guid.NewGuid();

        private static readonly TestingSiloOptions testSiloOptions = new TestingSiloOptions
        {
            SiloConfigFile = new FileInfo("Config_AzureTableStorage.xml"),
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = false,
            AdjustConfig = config =>
            {
                config.Globals.ServiceId = initialServiceId;
            }
        };

        // Use ClassCleanup to run code after all tests in a class have run
        [ClassCleanup]
        public static void ClassCleanup()
        {
            //ResetDefaultRuntimes();
            StopAllSilos();
        }

        public PersistenceGrainTests_AzureStore()
            : base(testSiloOptions)
        {
            timingFactor = CalibrateTimings();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureStore_Delete()
        {
            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain>(id);

            await grain.DoWrite(1);

            await grain.DoDelete();

            int val = await grain.GetValue(); // Should this throw instead?
            Assert.AreEqual(0, val, "Value after Delete");

            await grain.DoWrite(2);

            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Delete + New Write");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureStore_Read()
        {
            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain>(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKey_AzureStore_Read_Write()
        {
            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain>(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKey_AzureStore_Read_Write()
        {
            long id = random.Next();
            IAzureStorageTestGrain_LongKey grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain_LongKey>(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKeyExtended_AzureStore_Read_Write()
        {
            long id = random.Next();
            string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

            IAzureStorageTestGrain_LongExtendedKey
                grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain_LongExtendedKey>(id, extKey, null);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();
            Assert.AreEqual(2, val, "Value after DoRead");

            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Re-Read");

            string extKeyValue = await grain.GetExtendedKeyValue();
            Assert.AreEqual(extKey, extKeyValue, "Extended Key");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKeyExtended_AzureStore_Read_Write()
        {
            var id = Guid.NewGuid();
            string extKey = random.Next().ToString(CultureInfo.InvariantCulture);

            IAzureStorageTestGrain_GuidExtendedKey
                grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain_GuidExtendedKey>(id, extKey, null);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();
            Assert.AreEqual(2, val, "Value after DoRead");

            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Re-Read");

            string extKeyValue = await grain.GetExtendedKeyValue();
            Assert.AreEqual(extKey, extKeyValue, "Extended Key");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureStore_Read_Write()
        {
            long id = random.Next();

            IAzureStorageGenericGrain<int> grain = GrainClient.GrainFactory.GetGrain<IAzureStorageGenericGrain<int>>(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureStore_DiffTypes()
        {
            long id1 = random.Next();
            long id2 = id1;
            long id3 = id1;

            IAzureStorageGenericGrain<int> grain1 = GrainClient.GrainFactory.GetGrain<IAzureStorageGenericGrain<int>>(id1);

            IAzureStorageGenericGrain<string> grain2 = GrainClient.GrainFactory.GetGrain<IAzureStorageGenericGrain<string>>(id2);

            IAzureStorageGenericGrain<double> grain3 = GrainClient.GrainFactory.GetGrain<IAzureStorageGenericGrain<double>>(id3);

            int val1 = await grain1.GetValue();
            Assert.AreEqual(0, val1, "Initial value - 1");

            string val2 = await grain2.GetValue();
            Assert.AreEqual(null, val2, "Initial value - 2");

            double val3 = await grain3.GetValue();
            Assert.AreEqual(0.0, val3, "Initial value - 3");

            int expected1 = 1;
            await grain1.DoWrite(expected1);
            val1 = await grain1.GetValue();
            Assert.AreEqual(expected1, val1, "Value after Write#1 - 1");

            string expected2 = "Three";
            await grain2.DoWrite(expected2);
            val2 = await grain2.GetValue();
            Assert.AreEqual(expected2, val2, "Value after Write#1 - 2");

            double expected3 = 5.1;
            await grain3.DoWrite(expected3);
            val3 = await grain3.GetValue();
            Assert.AreEqual(expected3, val3, "Value after Write#1 - 3");

            val1 = await grain1.GetValue();
            Assert.AreEqual(expected1, val1, "Value before Write#2 - 1");
            expected1 = 2;
            await grain1.DoWrite(expected1);
            val1 = await grain1.GetValue();
            Assert.AreEqual(expected1, val1, "Value after Write#2 - 1");
            val1 = await grain1.DoRead();
            Assert.AreEqual(expected1, val1, "Value after Re-Read - 1");

            val2 = await grain2.GetValue();
            Assert.AreEqual(expected2, val2, "Value before Write#2 - 2");
            expected2 = "Four";
            await grain2.DoWrite(expected2);
            val2 = await grain2.GetValue();
            Assert.AreEqual(expected2, val2, "Value after Write#2 - 2");
            val2 = await grain2.DoRead();
            Assert.AreEqual(expected2, val2, "Value after Re-Read - 2");

            val3 = await grain3.GetValue();
            Assert.AreEqual(expected3, val3, "Value before Write#2 - 3");
            expected3 = 6.2;
            await grain3.DoWrite(expected3);
            val3 = await grain3.GetValue();
            Assert.AreEqual(expected3, val3, "Value after Write#2 - 3");
            val3 = await grain3.DoRead();
            Assert.AreEqual(expected3, val3, "Value after Re-Read - 3");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureStore_SiloRestart()
        {
            var initialDeploymentId = DeploymentId;
            Console.WriteLine("DeploymentId={0} ServiceId={1}", DeploymentId, Globals.ServiceId);

            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain>(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);

            Console.WriteLine("About to reset Silos");
            RestartDefaultSilos(true);
            Console.WriteLine("Silos restarted");

            Console.WriteLine("DeploymentId={0} ServiceId={1}", DeploymentId, Globals.ServiceId);
            Assert.AreEqual(initialServiceId, Globals.ServiceId, "ServiceId same after restart.");
            Assert.AreNotEqual(initialDeploymentId, DeploymentId, "DeploymentId different after restart.");

            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Activate()
        {
            const string testName = "Persistence_Perf_Activate";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxReadTime * n);

            // Timings for Activate
            RunPerfTest(n, testName, target,
                grainNoState => grainNoState.PingAsync(),
                grainMemory => grainMemory.DoSomething(),
                grainMemoryStore => grainMemoryStore.GetValue(),
                grainAzureTable => grainAzureTable.GetValue());
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write()
        {
            const string testName = "Persistence_Perf_Write";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxWriteTime * n);

            // Timings for Write
            RunPerfTest(n, testName, target,
                grainNoState => grainNoState.EchoAsync(testName),
                grainMemory => grainMemory.DoWrite(n),
                grainMemoryStore => grainMemoryStore.DoWrite(n),
                grainAzureTable => grainAzureTable.DoWrite(n));
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread()
        {
            const string testName = "Persistence_Perf_Write_Read";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxWriteTime * n);

            // Timings for Write
            RunPerfTest(n, testName + "--Write", target,
                grainNoState => grainNoState.EchoAsync(testName),
                grainMemory => grainMemory.DoWrite(n),
                grainMemoryStore => grainMemoryStore.DoWrite(n),
                grainAzureTable => grainAzureTable.DoWrite(n));

            // Timings for Activate
            RunPerfTest(n, testName + "--ReRead", target,
                grainNoState => grainNoState.GetLastEchoAsync(),
                grainMemory => grainMemory.DoRead(),
                grainMemoryStore => grainMemoryStore.DoRead(),
                grainAzureTable => grainAzureTable.DoRead());
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureStore_ConvertToFromStorageFormat_GrainReference()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid id = Guid.NewGuid();
            IUser grain = GrainClient.GrainFactory.GetGrain<IUser>(id);

            var initialState = new GrainStateContainingGrainReferences { Grain = grain };
            var entity = new AzureTableStorage.GrainStateEntity();
            var storage = new AzureTableStorage();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            Assert.IsNotNull(entity.Data, "Entity.Data");
            var convertedState = new GrainStateContainingGrainReferences();
            convertedState = (GrainStateContainingGrainReferences)storage.ConvertFromStorageFormat(entity);
            Assert.IsNotNull(convertedState, "Converted state");
            Assert.AreEqual(initialState.Grain, convertedState.Grain, "Grain");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureStore_ConvertToFromStorageFormat_GrainReference_List()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid[] ids = {Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()};
            IUser[] grains = new IUser[3];
            grains[0] = GrainClient.GrainFactory.GetGrain<IUser>(ids[0]);
            grains[1] = GrainClient.GrainFactory.GetGrain<IUser>(ids[1]);
            grains[2] = GrainClient.GrainFactory.GetGrain<IUser>(ids[2]);

            var initialState = new GrainStateContainingGrainReferences();
            foreach (var g in grains)
            {
                initialState.GrainList.Add(g);
                initialState.GrainDict.Add(g.GetPrimaryKey().ToString(), g);
            }
            var entity = new AzureTableStorage.GrainStateEntity();
            var storage = new AzureTableStorage();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            Assert.IsNotNull(entity.Data, "Entity.Data");
            var convertedState = (GrainStateContainingGrainReferences)storage.ConvertFromStorageFormat(entity);
            Assert.IsNotNull(convertedState, "Converted state");
            Assert.AreEqual(initialState.GrainList.Count, convertedState.GrainList.Count, "GrainList size");
            Assert.AreEqual(initialState.GrainDict.Count, convertedState.GrainDict.Count, "GrainDict size");
            for (int i = 0; i < grains.Length; i++)
            {
                string iStr = ids[i].ToString();
                Assert.AreEqual(initialState.GrainList[i], convertedState.GrainList[i], "GrainList #{0}", i);
                Assert.AreEqual(initialState.GrainDict[iStr], convertedState.GrainDict[iStr], "GrainDict #{0}", i);
            }
            Assert.AreEqual(initialState.Grain, convertedState.Grain, "Grain");
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void Persistence_Silo_StorageProvider_Azure()
        {
            List<SiloHandle> silos = GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                string provider = typeof(AzureTableStorage).FullName;
                List<string> providers = silo.Silo.TestHook.GetStorageProviderNames().ToList();
                Assert.IsTrue(providers.Contains(provider), "No storage provider found: {0}", provider);
            }
        }

        #region Utility functions
        // ---------- Utility functions ----------

        private void RunPerfTest(int n, string testName, TimeSpan target,
            Func<IEchoTaskGrain, Task> actionNoState,
            Func<IPersistenceTestGrain, Task> actionMemory,
            Func<IMemoryStorageTestGrain, Task> actionMemoryStore,
            Func<IAzureStorageTestGrain, Task> actionAzureTable)
        {
            IEchoTaskGrain[] noStateGrains = new IEchoTaskGrain[n];
            IPersistenceTestGrain[] memoryGrains = new IPersistenceTestGrain[n];
            IAzureStorageTestGrain[] azureStoreGrains = new IAzureStorageTestGrain[n];
            IMemoryStorageTestGrain[] memoryStoreGrains = new IMemoryStorageTestGrain[n];

            for (int i = 0; i < n; i++)
            {
                Guid id = Guid.NewGuid();
                noStateGrains[i] = GrainClient.GrainFactory.GetGrain<IEchoTaskGrain>(id);
                memoryGrains[i] = GrainClient.GrainFactory.GetGrain<IPersistenceTestGrain>(id);
                azureStoreGrains[i] = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain>(id);
                memoryStoreGrains[i] = GrainClient.GrainFactory.GetGrain<IMemoryStorageTestGrain>(id);
            }

            TimeSpan baseline, elapsed;

            elapsed = baseline = TimeRun(n, TimeSpan.Zero, testName + " (No state)",
                () => RunIterations(testName, n, i => actionNoState(noStateGrains[i])));

            elapsed = TimeRun(n, baseline, testName + " (Local Memory Store)",
                () => RunIterations(testName, n, i => actionMemory(memoryGrains[i])));

            elapsed = TimeRun(n, baseline, testName + " (Dev Store Grain Store)",
                () => RunIterations(testName, n, i => actionMemoryStore(memoryStoreGrains[i])));

            elapsed = TimeRun(n, baseline, testName + " (Azure Table Store)",
                () => RunIterations(testName, n, i => actionAzureTable(azureStoreGrains[i])));

            if (elapsed > target.Multiply(timingFactor))
            {
                string msg = string.Format("{0}: Elapsed time {1} exceeds target time {2}", testName, elapsed, target);

                if (elapsed > target.Multiply(2.0 * timingFactor))
                {
                    Assert.Fail(msg);
                }
                else
                {
                    Assert.Inconclusive(msg);
                }
            }
        }

        private static void RunIterations(string testName, int n, Func<int, Task> action)
        {
            List<Task> promises = new List<Task>();
            Stopwatch sw = Stopwatch.StartNew();
            // Fire off requests in batches
            for (int i = 0; i < n; i++)
            {
                var promise = action(i);
                promises.Add(promise);
                if ((i % BatchSize) == 0 && i > 0)
                {
                    Task.WaitAll(promises.ToArray(), AzureTableDefaultPolicies.TableCreationTimeout);
                    promises.Clear();
                    //Console.WriteLine("{0} has done {1} iterations  in {2} at {3} RPS",
                    //                  testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
                }
            }
            Task.WaitAll(promises.ToArray(), AzureTableDefaultPolicies.TableCreationTimeout);
            sw.Stop();
            Console.WriteLine("{0} completed. Did {1} iterations in {2} at {3} RPS",
                              testName, n, sw.Elapsed, n / sw.Elapsed.TotalSeconds);
        }
        #endregion
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

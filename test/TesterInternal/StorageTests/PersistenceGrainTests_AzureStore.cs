//#define REREAD_STATE_AFTER_WRITE_FAILED

using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans;
using Orleans.AzureUtils;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Tester;
using UnitTests.GrainInterfaces;
using UnitTests.Tester;
using Xunit;
using System.IO;
using Xunit.Abstractions;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace UnitTests.StorageTests
{
    /// <summary>
    /// Base_PersistenceGrainTests - a base class for testing persistence providers
    /// </summary>
    public abstract class Base_PersistenceGrainTests_AzureStore : OrleansTestingBase
    {
        private readonly ITestOutputHelper output;
        protected TestingSiloHost HostedCluster { get; private set; }
        private readonly double timingFactor;

        private const int LoopIterations_Grain = 1000;
        private const int BatchSize = 100;

        private const int MaxReadTime = 200;
        private const int MaxWriteTime = 2000;

        public Base_PersistenceGrainTests_AzureStore(ITestOutputHelper output, BaseClusterFixture fixture)
        {
            this.output = output;
            HostedCluster = fixture.HostedCluster;
            timingFactor = TestUtils.CalibrateTimings();
        }

        protected async Task Grain_AzureStore_Delete()
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

        protected async Task Grain_AzureStore_Read()
        {
            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain>(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");
        }

        protected async Task Grain_GuidKey_AzureStore_Read_Write()
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

        protected async Task Grain_LongKey_AzureStore_Read_Write()
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

        protected async Task Grain_LongKeyExtended_AzureStore_Read_Write()
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

        protected async Task Grain_GuidKeyExtended_AzureStore_Read_Write()
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

        protected async Task Grain_Generic_AzureStore_Read_Write()
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

        protected async Task Grain_Generic_AzureStore_DiffTypes()
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

        protected async Task Grain_AzureStore_SiloRestart()
        {
            var initialServiceId = this.HostedCluster.Globals.ServiceId;
            var initialDeploymentId = this.HostedCluster.DeploymentId;

            output.WriteLine("DeploymentId={0} ServiceId={1}", this.HostedCluster.DeploymentId, this.HostedCluster.Globals.ServiceId);

            Guid id = Guid.NewGuid();
            IAzureStorageTestGrain grain = GrainClient.GrainFactory.GetGrain<IAzureStorageTestGrain>(id);

            int val = await grain.GetValue();

            Assert.AreEqual(0, val, "Initial value");

            await grain.DoWrite(1);

            output.WriteLine("About to reset Silos");
            this.HostedCluster.RestartDefaultSilos(true);
            output.WriteLine("Silos restarted");

            output.WriteLine("DeploymentId={0} ServiceId={1}", this.HostedCluster.DeploymentId, this.HostedCluster.Globals.ServiceId);
            Assert.AreEqual(initialServiceId, this.HostedCluster.Globals.ServiceId, "ServiceId same after restart.");
            Assert.AreNotEqual(initialDeploymentId, this.HostedCluster.DeploymentId, "DeploymentId different after restart.");

            val = await grain.GetValue();
            Assert.AreEqual(1, val, "Value after Write-1");

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.AreEqual(2, val, "Value after Write-2");

            val = await grain.DoRead();

            Assert.AreEqual(2, val, "Value after Re-Read");
        }

        protected void Persistence_Perf_Activate()
        {
            const string testName = "Persistence_Perf_Activate";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxReadTime * n);

            // Timings for Activate
            RunPerfTest(n, testName, target,
                grainNoState => grainNoState.PingAsync(),
                grainMemory => grainMemory.DoSomething(),
                grainMemoryStore => grainMemoryStore.GetValue(),
                grainAzureStore => grainAzureStore.GetValue());
        }

        protected void Persistence_Perf_Write()
        {
            const string testName = "Persistence_Perf_Write";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxWriteTime * n);

            // Timings for Write
            RunPerfTest(n, testName, target,
                grainNoState => grainNoState.EchoAsync(testName),
                grainMemory => grainMemory.DoWrite(n),
                grainMemoryStore => grainMemoryStore.DoWrite(n),
                grainAzureStore => grainAzureStore.DoWrite(n));
        }

        protected void Persistence_Perf_Write_Reread()
        {
            const string testName = "Persistence_Perf_Write_Read";
            int n = LoopIterations_Grain;
            TimeSpan target = TimeSpan.FromMilliseconds(MaxWriteTime * n);

            // Timings for Write
            RunPerfTest(n, testName + "--Write", target,
                grainNoState => grainNoState.EchoAsync(testName),
                grainMemory => grainMemory.DoWrite(n),
                grainMemoryStore => grainMemoryStore.DoWrite(n),
                grainAzureStore => grainAzureStore.DoWrite(n));

            // Timings for Activate
            RunPerfTest(n, testName + "--ReRead", target,
                grainNoState => grainNoState.GetLastEchoAsync(),
                grainMemory => grainMemory.DoRead(),
                grainMemoryStore => grainMemoryStore.DoRead(),
                grainAzureStore => grainAzureStore.DoRead());
        }

       
        protected void Persistence_Silo_StorageProvider_Azure(Type providerType)
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                string provider = providerType.FullName;
                List<string> providers = silo.Silo.TestHook.GetStorageProviderNames().ToList();
                Assert.IsTrue(providers.Contains(provider), "No storage provider found: {0}", provider);
            }
        }

        #region Utility functions
        // ---------- Utility functions ----------

        protected void RunPerfTest(int n, string testName, TimeSpan target,
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

            elapsed = baseline = TestUtils.TimeRun(n, TimeSpan.Zero, testName + " (No state)",
                () => RunIterations(testName, n, i => actionNoState(noStateGrains[i])));

            elapsed = TestUtils.TimeRun(n, baseline, testName + " (Local Memory Store)",
                () => RunIterations(testName, n, i => actionMemory(memoryGrains[i])));

            elapsed = TestUtils.TimeRun(n, baseline, testName + " (Dev Store Grain Store)",
                () => RunIterations(testName, n, i => actionMemoryStore(memoryStoreGrains[i])));

            elapsed = TestUtils.TimeRun(n, baseline, testName + " (Azure Table Store)",
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
                    throw new SkipException(msg);
                }
            }
        }

        private void RunIterations(string testName, int n, Func<int, Task> action)
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
                    //output.WriteLine("{0} has done {1} iterations  in {2} at {3} RPS",
                    //                  testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
                }
            }
            Task.WaitAll(promises.ToArray(), AzureTableDefaultPolicies.TableCreationTimeout);
            sw.Stop();
            output.WriteLine("{0} completed. Did {1} iterations in {2} at {3} RPS",
                              testName, n, sw.Elapsed, n / sw.Elapsed.TotalSeconds);
        }
        #endregion
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

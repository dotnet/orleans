//#define REREAD_STATE_AFTER_WRITE_FAILED


using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;
using Orleans;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;
using TestExtensions.Runners;
using UnitTests.GrainInterfaces;
using AzureStoragePolicyOptions = Orleans.Clustering.AzureStorage.AzureStoragePolicyOptions;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace Tester.AzureUtils.Persistence
{
    /// <summary>
    /// Base_PersistenceGrainTests - a base class for testing persistence providers
    /// </summary>
    public abstract class Base_PersistenceGrainTests_AzureStore : OrleansTestingBase
    {
        private readonly ITestOutputHelper output;
        protected TestCluster HostedCluster { get; private set; }
        private readonly double timingFactor;
        protected readonly ILogger logger;
        private const int LoopIterations_Grain = 1000;
        private const int BatchSize = 100;
        private GrainPersistenceTestsRunner basicPersistenceTestsRunner;
        private const int MaxReadTime = 200;
        private const int MaxWriteTime = 2000;
        public class SiloBuilderConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.UseAzureStorageClustering(options =>
                {
                    options.ConfigureTestDefaults();
                });
            }
        }

        public class ClientBuilderConfigurator : IClientBuilderConfigurator
        {
            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.UseAzureStorageClustering(gatewayOptions => { gatewayOptions.ConfigureTestDefaults(); });
            }
        }

        public Base_PersistenceGrainTests_AzureStore(ITestOutputHelper output, BaseTestClusterFixture fixture, string grainNamespace = "UnitTests.Grains")
        {
            this.output = output;
            this.logger = fixture.Logger;
            HostedCluster = fixture.HostedCluster;
            GrainFactory = fixture.GrainFactory;
            timingFactor = TestUtils.CalibrateTimings();
            this.basicPersistenceTestsRunner = new GrainPersistenceTestsRunner(output, fixture, grainNamespace);
        }

        public IGrainFactory GrainFactory { get; }

        protected Task Grain_AzureStore_Delete()
        {
            return this.basicPersistenceTestsRunner.Grain_GrainStorage_Delete();
        }

        protected Task Grain_AzureStore_Read()
        {
            return this.basicPersistenceTestsRunner.Grain_GrainStorage_Read();
        }

        protected Task Grain_GuidKey_AzureStore_Read_Write()
        {
            return this.basicPersistenceTestsRunner.Grain_GuidKey_GrainStorage_Read_Write();
        }

        protected Task Grain_LongKey_AzureStore_Read_Write()
        {
            return this.basicPersistenceTestsRunner.Grain_LongKey_GrainStorage_Read_Write();
        }

        protected Task Grain_LongKeyExtended_AzureStore_Read_Write()
        {
            return this.basicPersistenceTestsRunner.Grain_LongKeyExtended_GrainStorage_Read_Write();
        }

        protected Task Grain_GuidKeyExtended_AzureStore_Read_Write()
        {
            return this.basicPersistenceTestsRunner.Grain_GuidKeyExtended_GrainStorage_Read_Write();
        }

        protected Task Grain_Generic_AzureStore_Read_Write()
        {
            return this.basicPersistenceTestsRunner.Grain_Generic_GrainStorage_Read_Write();
        }

        protected Task Grain_Generic_AzureStore_DiffTypes()
        {
            return this.basicPersistenceTestsRunner.Grain_Generic_GrainStorage_DiffTypes();
        }

        protected Task Grain_AzureStore_SiloRestart()
        {
            return this.basicPersistenceTestsRunner.Grain_GrainStorage_SiloRestart();
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

       
        protected async Task Persistence_Silo_StorageProvider_Azure(string providerName)
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                var testHooks = this.HostedCluster.Client.GetTestHooks(silo);
                List<string> providers = (await testHooks.GetStorageProviderNames()).ToList();
                Assert.True(providers.Contains(providerName), $"No storage provider found: {providerName}");
            }
        }

        // ---------- Utility functions ----------

        protected void RunPerfTest(int n, string testName, TimeSpan target,
            Func<IEchoTaskGrain, Task> actionNoState,
            Func<IPersistenceTestGrain, Task> actionMemory,
            Func<IMemoryStorageTestGrain, Task> actionMemoryStore,
            Func<IGrainStorageTestGrain, Task> actionAzureTable)
        {
            IEchoTaskGrain[] noStateGrains = new IEchoTaskGrain[n];
            IPersistenceTestGrain[] memoryGrains = new IPersistenceTestGrain[n];
            IGrainStorageTestGrain[] azureStoreGrains = new IGrainStorageTestGrain[n];
            IMemoryStorageTestGrain[] memoryStoreGrains = new IMemoryStorageTestGrain[n];

            for (int i = 0; i < n; i++)
            {
                Guid id = Guid.NewGuid();
                noStateGrains[i] = this.GrainFactory.GetGrain<IEchoTaskGrain>(id);
                memoryGrains[i] = this.GrainFactory.GetGrain<IPersistenceTestGrain>(id);
                azureStoreGrains[i] = this.GrainFactory.GetGrain<IGrainStorageTestGrain>(id);
                memoryStoreGrains[i] = this.GrainFactory.GetGrain<IMemoryStorageTestGrain>(id);
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
                    Assert.True(false, msg);
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
                    Task.WaitAll(promises.ToArray(), new AzureStoragePolicyOptions().CreationTimeout);
                    promises.Clear();
                    //output.WriteLine("{0} has done {1} iterations  in {2} at {3} RPS",
                    //                  testName, i, sw.Elapsed, i / sw.Elapsed.TotalSeconds);
                }
            }
            Task.WaitAll(promises.ToArray(), new AzureStoragePolicyOptions().CreationTimeout);
            sw.Stop();
            output.WriteLine("{0} completed. Did {1} iterations in {2} at {3} RPS",
                              testName, n, sw.Elapsed, n / sw.Elapsed.TotalSeconds);
        }
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

//#define REREAD_STATE_AFTER_WRITE_FAILED

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;
using Tester;
using Orleans.Runtime.Configuration;
using TestExtensions;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace UnitTests.StorageTests
{
    /// <summary>
    /// PersistenceGrainTests - Run with only local unit test silo -- no external dependency on Azure storage
    /// </summary>
    public class PersistenceGrainTests_Local : OrleansTestingBase, IClassFixture<PersistenceGrainTests_Local.Fixture>, IDisposable
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                var options = new TestClusterOptions(initialSilosCount: 1);
                options.ClusterConfiguration.Globals.RegisterStorageProvider<MockStorageProvider>("test1");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<MockStorageProvider>("test2", new Dictionary<string, string> { { "Config1", "1" }, { "Config2", "2" } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<ErrorInjectionStorageProvider>("ErrorInjector");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<MockStorageProvider>("lowercase");
                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");

                return new TestCluster(options);
            }
        }

        const string ErrorInjectorStorageProvider = "ErrorInjector";
        private readonly ITestOutputHelper output;
        protected TestCluster HostedCluster { get; }

        public PersistenceGrainTests_Local(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            HostedCluster = fixture.HostedCluster;
            SetErrorInjection(ErrorInjectorStorageProvider, ErrorInjectionPoint.None);
            ResetMockStorageProvidersHistory();
        }

        public void Dispose()
        {
            SetErrorInjection(ErrorInjectorStorageProvider, ErrorInjectionPoint.None);
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Silo_StorageProviders()
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                ICollection<string> providers = await silo.TestHook.GetStorageProviderNames();
                Assert.NotNull(providers); // Null provider manager
                Assert.True(providers.Count > 0, "Some providers loaded");
                const string providerName = "test1";
                IStorageProvider storageProvider = silo.AppDomainTestHook.GetStorageProvider(providerName);
                Assert.NotNull(storageProvider);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public void Persistence_Silo_StorageProvider_Name_LowerCase()
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                const string providerName = "LowerCase";
                IStorageProvider storageProvider = silo.AppDomainTestHook.GetStorageProvider(providerName);
                Assert.NotNull(storageProvider);
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public void Persistence_Silo_StorageProvider_Name_Missing()
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            var silo = silos.First();
            const string providerName = "NotPresent";
            Assert.Throws<KeyNotFoundException>(() =>
            {
                IStorageProvider store = silo.AppDomainTestHook.GetStorageProvider(providerName);
            });
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_CheckStateInit()
        {
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);
            bool ok = await grain.CheckStateInit();
            Assert.True(ok, "CheckStateInit OK");
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_CheckStorageProvider()
        {
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);
            string providerType = await grain.CheckProviderType();
            Assert.Equal(typeof(MockStorageProvider).FullName, providerType); // StorageProvider provider type
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Init()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.Equal(1, storageProvider.InitCount); // StorageProvider #Init
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Activate_StoredValue()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceTestGrain).FullName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");

            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue<PersistenceTestGrainState>(providerName, grainType, grain, "Field1", initialValue);

            int readValue = await grain.GetValue();
            Assert.Equal(initialValue, readValue); // Read previously stored value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Generics")]
        public async Task Persistence_Grain_Activate_StoredValue_Generic() 
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceTestGenericGrain<int>).FullName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");

            var grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGenericGrain<int>>(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue<PersistenceTestGrainState>(providerName, grainType, grain, "Field1", initialValue);

            int readValue = await grain.GetValue();
            Assert.Equal(initialValue, readValue); // Read previously stored value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Activate_Error()
        {
            const string providerName = ErrorInjectorStorageProvider;
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");

            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue<PersistenceTestGrainState>(providerName, grainType, grain, "Field1", initialValue);

            SetErrorInjection(providerName, ErrorInjectionPoint.BeforeRead);

            await Assert.ThrowsAsync<OrleansException>(() =>
                grain.GetValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Read()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(0, storageProvider.WriteCount); // StorageProvider #Writes
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Write()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(1, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(1, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            await grain.DoWrite(2);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(2, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_ReRead()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceTestGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(guid);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(0, storageProvider.WriteCount); // StorageProvider #Writes
            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", 42); // Update state data behind grain


            await grain.DoRead();

            Assert.Equal(2, storageProvider.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(0, storageProvider.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(42, ((PersistenceTestGrainState)storageProvider.LastState).Field1); // Store-Field1
        }
        
        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_Read_Write()
        {
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IMemoryStorageTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IMemoryStorageTestGrain>(guid);

            int val = await grain.GetValue();

            Assert.Equal(0, val); // Initial value

            await grain.DoWrite(1);
            val = await grain.GetValue();
            Assert.Equal(1, val); // Value after Write-1

            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.Equal(2, val); // Value after Write-2

            val = await grain.DoRead();

            Assert.Equal(2, val); // Value after Re-Read
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_Delete()
        {
            Guid id = Guid.NewGuid();
            var grain = this.HostedCluster.GrainFactory.GetGrain<IMemoryStorageTestGrain>(id);
            await grain.DoWrite(1);
            await grain.DoDelete();
            int val = await grain.GetValue(); // Should this throw instead?
            Assert.Equal(0, val); // Value after Delete
            await grain.DoWrite(2);
            val = await grain.GetValue();
            Assert.Equal(2, val); // Value after Delete + New Write
        }

        [Fact, TestCategory("Stress"), TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_Stress_Read()
        {
            const int numIterations = 10000;

            Stopwatch sw = Stopwatch.StartNew();

            Task<int>[] promises = new Task<int>[numIterations];
            for (int i = 0; i < numIterations; i++)
            {
                IMemoryStorageTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IMemoryStorageTestGrain>(Guid.NewGuid());
                int idx = i; // Capture
                Func<Task<int>> asyncFunc =
                    async () =>
                    {
                        await grain.DoWrite(idx);
                        return await grain.DoRead();
                    };
                promises[i] = Task.Run(asyncFunc);
            }
            await Task.WhenAll(promises);

            TimeSpan elapsed = sw.Elapsed;
            double tps = (numIterations * 2) / elapsed.TotalSeconds; // One Read and one Write per iteration
            output.WriteLine("{0} Completed Read-Write operations in {1} at {2} TPS",  numIterations, elapsed, tps);

            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                Assert.Equal(expectedVal,  promises[i].Result);  //  "Returned value - Read @ #" + i
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Write()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(1, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(1, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            await grain.DoWrite(2);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(2, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Delete()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            int initialReadCount = storageProvider.ReadCount;
            int initialWriteCount = storageProvider.WriteCount;
            int initialDeleteCount = storageProvider.DeleteCount;

            await grain.DoDelete();

            Assert.Equal(initialDeleteCount + 1, storageProvider.DeleteCount); // StorageProvider #Deletes
            Assert.Equal(null, storageProvider.LastState); // Store-AfterDelete-Empty

            int val = await grain.GetValue(); // Returns current in-memory null data without re-read.
            Assert.Equal(0, val); // Value after Delete
            Assert.Equal(initialReadCount, storageProvider.ReadCount); // StorageProvider #Reads

            await grain.DoWrite(2);

            Assert.Equal(initialReadCount, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(initialWriteCount + 1, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Read_Error()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceErrorGrain>(id);

            var val = await grain.GetValue();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(0, storageProvider.WriteCount); // StorageProvider #Writes

            try
            {
                await grain.DoReadError(true);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                Exception e = ae.GetBaseException();
                if (e is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw e;
                }
            }

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(0, storageProvider.WriteCount); // StorageProvider #Writes-2

            try
            {
                await grain.DoReadError(false);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                Exception e = ae.GetBaseException();
                if (e is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw e;
                }
            }

            Assert.Equal(2, storageProvider.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(0, storageProvider.WriteCount); // StorageProvider #Writes-2
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Write_Error()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceErrorGrain>(id);

            await grain.DoWrite(1);

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(1, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(1, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            await grain.DoWrite(2);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(2, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            try
            {
                await grain.DoWriteError(3, true);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                if (ae.GetBaseException() is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw;
                }
            }

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(2, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            try
            {
                await grain.DoWriteError(4, false);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                if (ae.GetBaseException() is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw;
                }
            }

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(3, storageProvider.WriteCount); // StorageProvider #Writes

            Assert.Equal(4, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_ReRead_Error()
        {
            const string providerName = "test1";
            string grainType = typeof(PersistenceErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceErrorGrain>(guid);

            var val = await grain.GetValue();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName);

            Assert.Equal(1, storageProvider.ReadCount); // StorageProvider #Reads
            Assert.Equal(0, storageProvider.WriteCount); // StorageProvider #Writes

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", 42);

            await grain.DoRead();

            Assert.Equal(2, storageProvider.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(0, storageProvider.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(42, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            await grain.DoWrite(43);

            Assert.Equal(2, storageProvider.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(1, storageProvider.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(43, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            try
            {
                await grain.DoReadError(true);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                if (ae.GetBaseException() is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw;
                }
            }

            Assert.Equal(2, storageProvider.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(1, storageProvider.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(43, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            try
            {
                await grain.DoReadError(false);
            }
            catch (ApplicationException)
            {
                // Expected error
            }
            catch (AggregateException ae)
            {
                if (ae.GetBaseException() is ApplicationException)
                {
                    // Expected error
                }
                else
                {
                    throw;
                }
            }

            Assert.Equal(3, storageProvider.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(1, storageProvider.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(43, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

            val = await grain.DoRead();

            Assert.Equal(expectedVal, val); // Returned value

            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 52;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

            val = await grain.DoRead();

            Assert.Equal(expectedVal, val); // Returned value

            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterRead);
            CheckStorageProviderErrors(grain.DoRead);

            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value

            int newVal = 53;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", newVal);

            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value

            await grain.DoRead(); // Force re-read
            expectedVal = newVal;

            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeWrite()
        {
            Guid id = Guid.NewGuid();
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(id);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 62;
            await grain.DoWrite(expectedVal);
            Assert.Equal(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            const int attemptedVal3 = 63;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal3));

            // Stored value unchanged
            Assert.Equal(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            // Stored value unchanged
            Assert.Equal(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1
#if REREAD_STATE_AFTER_WRITE_FAILED
            Assert.Equal(expectedVal, val); // Last value written successfully
#else
            Assert.Equal(attemptedVal3, val); // Last value attempted to be written is still in memory
#endif
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterWrite()
        {
            Guid id = Guid.NewGuid();
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(id);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 82;
            await grain.DoWrite(expectedVal);
            Assert.Equal(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            const int attemptedVal4 = 83;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal4));

            // Stored value has changed
            expectedVal = attemptedVal4;
            Assert.Equal(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeReRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 72;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);
            val = await grain.DoRead();
            Assert.Equal(expectedVal, val); // Returned value

            expectedVal = 73;
            await grain.DoWrite(expectedVal);
            Assert.Equal(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterReRead()
        {
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue();

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);

            int expectedVal = 92;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);
            val = await grain.DoRead();
            Assert.Equal(expectedVal, val); // Returned value

            expectedVal = 93;
            await grain.DoWrite(expectedVal);
            Assert.Equal(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1

            expectedVal = 94;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.AfterRead);
            CheckStorageProviderErrors(grain.DoRead);

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_Handled_Read()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

            val = await grain.DoRead(false);

            Assert.Equal(expectedVal, val); // Returned value

            int newVal = expectedVal + 1;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", newVal);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeRead);
            val = await grain.DoRead(true);
            Assert.Equal(expectedVal, val); // Returned value

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            expectedVal = newVal;

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", newVal);
            val = await grain.DoRead(false);
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_Handled_Write()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

            val = await grain.DoRead(false);

            Assert.Equal(expectedVal, val); // Returned value

            int newVal = expectedVal + 1;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeWrite);
            await grain.DoWrite(newVal, true);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            expectedVal = newVal;
            await grain.DoWrite(newVal, false);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_NotHandled_Write()
        {
            string grainType = typeof(PersistenceUserHandledErrorGrain).FullName;

            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            ErrorInjectionStorageProvider storageProvider = (ErrorInjectionStorageProvider)FindStorageProviderInUse(ErrorInjectorStorageProvider);
            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);
            storageProvider.SetValue<PersistenceTestGrainState>(grainType, (GrainReference)grain, "Field1", expectedVal);

            val = await grain.DoRead(false);

            Assert.Equal(expectedVal, val); // Returned value after read

            int newVal = expectedVal + 1;
            storageProvider.SetErrorInjection(ErrorInjectionPoint.BeforeWrite);
            CheckStorageProviderErrors(() => grain.DoWrite(newVal, false));

            val = await grain.GetValue();
            // Stored value unchanged
            Assert.Equal(expectedVal, storageProvider.GetLastState<PersistenceTestGrainState>().Field1); // Store-Field1
#if REREAD_STATE_AFTER_WRITE_FAILED
            Assert.Equal(expectedVal, val); // After failed write: Last value written successfully
#else
            Assert.Equal(newVal, val); // After failed write: Last value attempted to be written is still in memory
#endif

            storageProvider.SetErrorInjection(ErrorInjectionPoint.None);

            expectedVal = newVal;
            await grain.DoWrite(newVal, false);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value after good write
        }

        [Fact, TestCategory("Stress"), TestCategory("CorePerf"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Loop_Read()
        {
            const int numIterations = 100;

            string grainType = typeof(PersistenceTestGrain).FullName;

            Task<int>[] promises = new Task<int>[numIterations];
            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(Guid.NewGuid());
                Guid guid = grain.GetPrimaryKey();
                string id = guid.ToString("N");

                this.SetStoredValue<PersistenceTestGrainState>("test1", grainType, grain, "Field1", expectedVal); // Update state data behind grain
                promises[i] = grain.DoRead();
            }
            await Task.WhenAll(promises);

            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                Assert.Equal(expectedVal,  promises[i].Result);  //  "Returned value - Read @ #" + i
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_BadProvider()
        {
            IBadProviderTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IBadProviderTestGrain>(Guid.NewGuid());
            var oex = await Assert.ThrowsAsync<OrleansException>(() => grain.DoSomething());
            Assert.IsAssignableFrom<BadProviderConfigException>(oex.InnerException);
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public void OrleansException_BadProvider()
        {
            string msg1 = "BadProvider";
            string msg2 = "Wrapper";
            string msg3 = "Aggregate";

            var bpce = new BadProviderConfigException(msg1);
            var oe = new OrleansException(msg2, bpce);
            var ae = new AggregateException(msg3, oe);

            Assert.NotNull(ae.InnerException); // AggregateException.InnerException should not be null
            Assert.IsAssignableFrom<OrleansException>(ae.InnerException);
            Exception exc = ae.InnerException;
            Assert.NotNull(exc.InnerException); // OrleansException.InnerException should not be null
            Assert.IsAssignableFrom<BadProviderConfigException>(exc.InnerException);

            exc = ae.GetBaseException();
            Assert.NotNull(exc.InnerException); // BaseException.InnerException should not be null
            Assert.IsAssignableFrom<BadProviderConfigException>(exc.InnerException);

            Assert.Equal(msg3,  ae.Message);  //  "AggregateException.Message should be '{0}'", msg3
            Assert.Equal(msg2,  exc.Message);  //  "OrleansException.Message should be '{0}'", msg2
            Assert.Equal(msg1,  exc.InnerException.Message);  //  "InnerException.Message should be '{0}'", msg1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_UserGrain_Read_Write()
        {
            Guid id = Guid.NewGuid();
            IUser grain = this.HostedCluster.GrainFactory.GetGrain<IUser>(id);

            string name = id.ToString();

            await grain.SetName(name);

            string readName = await grain.GetName();

            Assert.Equal(name, readName); // Read back previously set name

            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();
            string name1 = id1.ToString();
            string name2 = id2.ToString();
            IUser friend1 = this.HostedCluster.GrainFactory.GetGrain<IUser>(id1);
            IUser friend2 = this.HostedCluster.GrainFactory.GetGrain<IUser>(id2);
            await friend1.SetName(name1);
            await friend2.SetName(name2);

            var readName1 = await friend1.GetName();
            var readName2 = await friend2.GetName();

            Assert.Equal(name1, readName1); // Friend #1 Name
            Assert.Equal(name2, readName2); // Friend #2 Name

            await grain.AddFriend(friend1);
            await grain.AddFriend(friend2);

            var friends = await grain.GetFriends();
            Assert.Equal(2, friends.Count); // Number of friends
            Assert.Equal(name1, await friends[0].GetName()); // GetFriends - Friend #1 Name
            Assert.Equal(name2, await friends[1].GetName()); // GetFriends - Friend #2 Name
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_NoState()
        {
            const string providerName = "test1";
            Guid id = Guid.NewGuid();
            IPersistenceNoStateTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceNoStateTestGrain>(id);

            await grain.DoSomething();

            MockStorageProvider storageProvider = FindStorageProviderInUse(providerName, true);

            Assert.Equal(null, storageProvider); // StorageProvider found
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Serialization")]
        public void Serialize_GrainState_DeepCopy()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            IUser[] grains = new IUser[3];
            grains[0] = this.HostedCluster.GrainFactory.GetGrain<IUser>(Guid.NewGuid());
            grains[1] = this.HostedCluster.GrainFactory.GetGrain<IUser>(Guid.NewGuid());
            grains[2] = this.HostedCluster.GrainFactory.GetGrain<IUser>(Guid.NewGuid());

            GrainStateContainingGrainReferences initialState = new GrainStateContainingGrainReferences();
            foreach (var g in grains)
            {
                initialState.GrainList.Add(g);
                initialState.GrainDict.Add(g.GetPrimaryKey().ToString(), g);
            }

            var copy = (GrainStateContainingGrainReferences)SerializationManager.DeepCopy(initialState);
            Assert.NotSame(initialState.GrainDict, copy.GrainDict); // Dictionary
            Assert.NotSame(initialState.GrainList, copy.GrainList); // List
        }

        [Fact, TestCategory("Persistence"), TestCategory("Serialization"), TestCategory("CorePerf"), TestCategory("Stress")]
        public async Task Serialize_GrainState_DeepCopy_Stress()
        {
            int num = 100;
            int loops = num * 100;
            GrainStateContainingGrainReferences[] states = new GrainStateContainingGrainReferences[num];
            for (int i = 0; i < num; i++)
            {
                IUser grain = this.HostedCluster.GrainFactory.GetGrain<IUser>(Guid.NewGuid());
                states[i] = new GrainStateContainingGrainReferences();
                states[i].GrainList.Add(grain);
                states[i].GrainDict.Add(grain.GetPrimaryKey().ToString(), grain);
            }

            List<Task> tasks = new List<Task>();
            for (int i = 0; i < loops; i++)
            {
                int idx = random.Next(num);
                tasks.Add(Task.Run(() => { var copy = SerializationManager.DeepCopy(states[idx]); }));
                tasks.Add(Task.Run(() => { var other = SerializationManager.RoundTripSerializationForTesting(states[idx]); }));
            }
            await Task.WhenAll(tasks);

            //Task copyTask = Task.Run(() =>
            //{
            //    for (int i = 0; i < loops; i++)
            //    {
            //        int idx = random.Next(num);
            //        var copy = states[idx].DeepCopy();
            //    }
            //});
            //Task serializeTask = Task.Run(() =>
            //{
            //    for (int i = 0; i < loops; i++)
            //    {
            //        int idx = random.Next(num);
            //        var other = SerializationManager.RoundTripSerializationForTesting(states[idx]);
            //    }
            //});
            //await Task.WhenAll(copyTask, serializeTask);
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task ReentrentGrainWithState()
        {
            Guid id1 = Guid.NewGuid();
            Guid id2 = Guid.NewGuid();
            IReentrentGrainWithState grain1 = this.HostedCluster.GrainFactory.GetGrain<IReentrentGrainWithState>(id1);
            IReentrentGrainWithState grain2 = this.HostedCluster.GrainFactory.GetGrain<IReentrentGrainWithState>(id2);
            await Task.WhenAll(grain1.Setup(grain2), grain2.Setup(grain1));

            Task t11 = grain1.Test1();
            Task t12 = grain1.Test2();
            Task t21 = grain2.Test1();
            Task t22 = grain2.Test2();
            await Task.WhenAll(t11, t12, t21, t22);
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task NonReentrentStressGrainWithoutState()
        {
            Guid id1 = Guid.NewGuid();
            INonReentrentStressGrainWithoutState grain1 = this.HostedCluster.GrainFactory.GetGrain<INonReentrentStressGrainWithoutState>(id1);
            await grain1.Test1();
        }

        private const bool DoStart = true; // Task.Delay tests fail (Timeout) unless True

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task ReentrentGrain_Task_Delay()
        {
            Guid id1 = Guid.NewGuid();
            IReentrentGrainWithState grain1 = this.HostedCluster.GrainFactory.GetGrain<IReentrentGrainWithState>(id1);

            await grain1.Task_Delay(DoStart);
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task NonReentrentGrain_Task_Delay()
        {
            Guid id1 = Guid.NewGuid();
            INonReentrentStressGrainWithoutState grain1 = this.HostedCluster.GrainFactory.GetGrain<INonReentrentStressGrainWithoutState>(id1);

            await grain1.Task_Delay(DoStart);
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Scheduler"), TestCategory("Reentrancy")]
        public async Task StateInheritanceTest()
        {
            Guid id1 = Guid.NewGuid();
            IStateInheritanceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IStateInheritanceTestGrain>(id1);

            await grain.SetValue(1);
            int val = await grain.GetValue();
            Assert.Equal(1, val);
        }
        
        #region Utility functions
        // ---------- Utility functions ----------
        private void SetStoredValue<TState>(string providerName, string grainType, IGrain grain, string fieldName, int newValue)
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                MockStorageProvider provider = (MockStorageProvider)siloHandle.AppDomainTestHook.GetStorageProvider(providerName);
                provider.SetValue<TState>(grainType, (GrainReference)grain, "Field1", newValue);
            }
        }

        private void SetErrorInjection(string providerName, ErrorInjectionPoint errorInjectionPoint)
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                ErrorInjectionStorageProvider provider = (ErrorInjectionStorageProvider)siloHandle.AppDomainTestHook.GetStorageProvider(providerName);
                provider.SetErrorInjection(errorInjectionPoint);
            }
        }

        private void CheckStorageProviderErrors(
            Func<Task> taskFunc)
        {
            StackTrace at = new StackTrace();
            TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15);
            try
            {
                bool ok = taskFunc().Wait(timeout);
                if (!ok) throw new TimeoutException();

                if (ErrorInjectionStorageProvider.DoInjectErrors)
                {
                    string msg = "StorageProviderInjectedError exception should have been thrown " + at;
                    output.WriteLine("Assertion failed: {0}", msg);
                    Assert.True(false, msg);
                }
            }
            catch (Exception e)
            {
                output.WriteLine("Exception caught: {0}", e);
                var exc = e.GetBaseException();
                if (exc is OrleansException)
                {
                    exc = exc.InnerException;
                }
                Assert.IsAssignableFrom<StorageProviderInjectedError>(exc);
                //if (exc is StorageProviderInjectedError)
                //{
                //     //Expected error
                //}
                //else
                //{
                //    output.WriteLine("Unexpected exception: {0}", exc);
                //    Assert.True(false, exc.ToString());
                //}
            }
        }

        private MockStorageProvider FindStorageProviderInUse(string providerName, bool okNull = false)
        {
            MockStorageProvider providerInUse = null;
            SiloHandle siloInUse = null;
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var siloHandle in silos)
            {
                MockStorageProvider provider = (MockStorageProvider)siloHandle.AppDomainTestHook.GetStorageProvider(providerName);
                Assert.NotNull(provider);
                if (provider.ReadCount > 0)
                {
                    Assert.Null(providerInUse);
                   
                    providerInUse = provider;
                    siloInUse = siloHandle;
                }
            }

            if (!okNull && providerInUse == null)
            {
                Assert.True(false, $"Cannot find active storage provider currently in use, Name={providerName}");
            }

            return providerInUse;
        }

        private void ResetMockStorageProvidersHistory()
        {
            var mockStorageProviders = new[] { "test1", "test2", "lowercase" };
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos().ToList())
            {
                foreach (var providerName in mockStorageProviders)
                {
                    MockStorageProvider provider = (MockStorageProvider)siloHandle.AppDomainTestHook.GetStorageProvider(providerName);
                    if (provider != null)
                    {
                        provider.ResetHistory();
                    }
                }
            }
        }
        #endregion
    }

    [Serializable]
    public class GrainStateContainingGrainReferences
    {
        public IAddressable Grain { get; set; }
        public List<IAddressable> GrainList { get; set; }
        public Dictionary<string, IAddressable> GrainDict { get; set; }

        public GrainStateContainingGrainReferences()
        {
            GrainList = new List<IAddressable>();
            GrainDict = new Dictionary<string, IAddressable>();
        }
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

//#define REREAD_STATE_AFTER_WRITE_FAILED

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using UnitTests.Grains;
using Xunit;
using Xunit.Abstractions;
using TesterInternal;
using TestExtensions;
using Orleans.Hosting;
using Orleans.Internal;
using Microsoft.Extensions.DependencyInjection;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace UnitTests.StorageTests
{
    /// <summary>
    /// PersistenceGrainTests - Run with only local unit test silo -- no external dependency on Azure storage
    /// </summary>
    public class PersistenceGrainTests_Local : OrleansTestingBase, IClassFixture<PersistenceGrainTests_Local.Fixture>, IDisposable, IAsyncLifetime
    {
        public class Fixture : BaseTestClusterFixture
        {

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 1;
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }

            private class SiloConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddMemoryGrainStorage("MemoryStore");
                    hostBuilder.AddTestStorageProvider(MockStorageProviderName1, (sp, name) => ActivatorUtilities.CreateInstance<MockStorageProvider>(sp, name));
                    hostBuilder.AddTestStorageProvider(MockStorageProviderName2, (sp, name) => ActivatorUtilities.CreateInstance<MockStorageProvider>(sp, name));
                    hostBuilder.AddTestStorageProvider(MockStorageProviderNameLowerCase, (sp, name) => ActivatorUtilities.CreateInstance<MockStorageProvider>(sp, name));
                    hostBuilder.AddTestStorageProvider(ErrorInjectorProviderName, (sp, name) => ActivatorUtilities.CreateInstance<ErrorInjectionStorageProvider>(sp));
                }
            }
        }

        const string DefaultGrainStateName = "state";
        const string MockStorageProviderName1 = "test1";
        const string MockStorageProviderName2 = "test2";
        const string MockStorageProviderNameLowerCase = "lowercase";
        const string ErrorInjectorProviderName = "ErrorInjector";
        private readonly ITestOutputHelper output;

        protected TestCluster HostedCluster { get; }

        public PersistenceGrainTests_Local(ITestOutputHelper output, Fixture fixture)
        {
            this.output = output;
            HostedCluster = fixture.HostedCluster;
            ResetMockStorageProvidersHistory();
        }

        public async Task InitializeAsync()
        {
            await SetErrorInjection(ErrorInjectorProviderName, ErrorInjectionPoint.None);
        }

        public async Task DisposeAsync()
        {
            await SetErrorInjection(ErrorInjectorProviderName, ErrorInjectionPoint.None);
        }

        public void Dispose()
        {
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Silo_StorageProvidersLoaded()
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            foreach (var silo in silos)
            {
                var testHooks = this.HostedCluster.Client.GetTestHooks(silo);
                ICollection<string> providers = await testHooks.GetStorageProviderNames();
                Assert.NotNull(providers); // Null provider manager
                Assert.True(providers.Count > 0, "Some providers loaded");
                Assert.True(testHooks.HasStorageProvider(MockStorageProviderName1).Result,
                    $"provider {MockStorageProviderName1} on silo {silo.Name} should be registered");
                Assert.True(testHooks.HasStorageProvider(MockStorageProviderName2).Result,
                    $"provider {MockStorageProviderName2} on silo {silo.Name} should be registered");
                Assert.True(testHooks.HasStorageProvider(MockStorageProviderNameLowerCase).Result,
                    $"provider {MockStorageProviderNameLowerCase} on silo {silo.Name} should be registered");
                Assert.True(testHooks.HasStorageProvider(ErrorInjectorProviderName).Result,
                    $"provider {ErrorInjectorProviderName} on silo {silo.Name} should be registered");
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public void Persistence_Silo_StorageProvider_Name_Missing()
        {
            List<SiloHandle> silos = this.HostedCluster.GetActiveSilos().ToList();
            var silo = silos.First();
            const string providerName = "NotPresent";
            Assert.False(this.HostedCluster.Client.GetTestHooks(silo).HasStorageProvider(providerName).Result,
                    $"provider {providerName} on silo {silo.Name} should not be registered");
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
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);
            await grain.DoSomething();

            //request InitCount on providers on all silos in this cluster
            IManagementGrain mgmtGrain = this.HostedCluster.GrainFactory.GetGrain<IManagementGrain>(0);
            object[] replies = await mgmtGrain.SendControlCommandToProvider(typeof(MockStorageProvider).FullName,
               MockStorageProviderName1, (int)MockStorageProvider.Commands.InitCount, null);

            Assert.Contains(1, replies); // StorageProvider #Init
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Activate_StoredValue()
        {
            const string providerName = MockStorageProviderName1;
            Guid guid = Guid.NewGuid();
            _ = guid.ToString("N");

            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue(providerName, typeof(MockStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", initialValue);

            int readValue = await grain.GetValue();
            Assert.Equal(initialValue, readValue); // Read previously stored value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Generics")]
        public async Task Persistence_Grain_Activate_StoredValue_Generic() 
        {
            const string providerName = MockStorageProviderName1;
            Guid guid = Guid.NewGuid();
            _ = guid.ToString("N");

            var grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGenericGrain<int>>(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue(providerName, typeof(MockStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", initialValue);

            int readValue = await grain.GetValue();
            Assert.Equal(initialValue, readValue); // Read previously stored value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Activate_Error()
        {
            const string providerName = ErrorInjectorProviderName;
            string grainType = typeof(PersistenceProviderErrorGrain).FullName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");

            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            // Store initial value in storage
            int initialValue = 567;
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, grainType, grain, "Field1", initialValue);

            await SetErrorInjection(providerName, ErrorInjectionPoint.BeforeRead);

            await Assert.ThrowsAsync<StorageProviderInjectedError>(() =>
                grain.GetValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Read()
        {
            const string providerName = MockStorageProviderName1;
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoSomething();
            var providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);

            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(0, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Write()
        {
            const string providerName = MockStorageProviderName1;
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            var providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);

            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(1, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(1, providerState.LastStoredGrainState.Field1); // Store-Field1

            await grain.DoWrite(2);
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(2, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, providerState.LastStoredGrainState.Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_ReRead()
        {
            const string providerName = MockStorageProviderName1;

            Guid guid = Guid.NewGuid();
            _ = guid.ToString("N");
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(guid);

            await grain.DoSomething();

            var providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);

            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(0, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes
            SetStoredValue(providerName, typeof(MockStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", 42);
            
            await grain.DoRead();
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(2, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(0, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(42, providerState.LastStoredGrainState.Field1); // Store-Field1
        }
        
        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("MemoryStore")]
        public async Task MemoryStore_Read_Write()
        {
            Guid guid = Guid.NewGuid();
            _ = guid.ToString("N");
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
            const string providerName = MockStorageProviderName1;
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            var providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);

            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(1, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(1, providerState.LastStoredGrainState.Field1); // Store-Field1

            await grain.DoWrite(2);
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(2, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, providerState.LastStoredGrainState.Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Delete()
        {
            const string providerName = MockStorageProviderName1;
            Guid id = Guid.NewGuid();
            IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(id);

            await grain.DoWrite(1);

            var providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);

            int initialReadCount = providerState.ProviderStateForTest.ReadCount;
            int initialWriteCount = providerState.ProviderStateForTest.WriteCount;
            int initialDeleteCount = providerState.ProviderStateForTest.DeleteCount;

            await grain.DoDelete();
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(initialDeleteCount + 1, providerState.ProviderStateForTest.DeleteCount); // StorageProvider #Deletes
            Assert.Null(providerState.LastStoredGrainState); // Store-AfterDelete-Empty

            int val = await grain.GetValue(); // Returns current in-memory null data without re-read.
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName); // update state
            Assert.Equal(0, val); // Value after Delete
            Assert.Equal(initialReadCount, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads

            await grain.DoWrite(2);
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName); // update state
            Assert.Equal(initialReadCount, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(initialWriteCount + 1, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, providerState.LastStoredGrainState.Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Read_Error()
        {
            const string providerName = MockStorageProviderName1;
            Guid id = Guid.NewGuid();
            IPersistenceErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceErrorGrain>(id);
            _ = await grain.GetValue();

            var providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);

            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(0, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

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

            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(0, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes-2

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
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(2, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(0, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes-2
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_Write_Error()
        {
            const string providerName = MockStorageProviderName1;
            Guid id = Guid.NewGuid();
            IPersistenceErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceErrorGrain>(id);

            await grain.DoWrite(1);

            var providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);

            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(1, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(1, providerState.LastStoredGrainState.Field1); // Store-Field1

            await grain.DoWrite(2);
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(2, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, providerState.LastStoredGrainState.Field1); // Store-Field1

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
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName); // update provider state
            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(2, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(2, providerState.LastStoredGrainState.Field1); // Store-Field1

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
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(3, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            Assert.Equal(4, providerState.LastStoredGrainState.Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Grain_ReRead_Error()
        {
            const string providerName = "test1";

            Guid guid = Guid.NewGuid();
            _ = guid.ToString("N");
            IPersistenceErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceErrorGrain>(guid);
            _ = await grain.GetValue();

            var providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);

            Assert.Equal(1, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads
            Assert.Equal(0, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes

            SetStoredValue(providerName, typeof(MockStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", 42);

            await grain.DoRead();
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(2, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(0, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(42, providerState.LastStoredGrainState.Field1); // Store-Field1

            await grain.DoWrite(43);
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(2, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(1, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(43, providerState.LastStoredGrainState.Field1); // Store-Field1

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
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(2, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(1, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(43, providerState.LastStoredGrainState.Field1); // Store-Field1

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
            providerState = GetStateForStorageProviderInUse(providerName, typeof(MockStorageProvider).FullName);
            Assert.Equal(3, providerState.ProviderStateForTest.ReadCount); // StorageProvider #Reads-2
            Assert.Equal(1, providerState.ProviderStateForTest.WriteCount); // StorageProvider #Writes-2

            Assert.Equal(43, providerState.LastStoredGrainState.Field1); // Store-Field1
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeRead()
        {
            string providerName = ErrorInjectorProviderName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal);
            val = await grain.DoRead();

            Assert.Equal(expectedVal, val); // Returned value
            await SetErrorInjection(providerName, ErrorInjectionPoint.BeforeRead);
            
            await CheckStorageProviderErrors(grain.DoRead);

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);

            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterRead()
        {
            string providerName = ErrorInjectorProviderName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 52;

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal);

            val = await grain.DoRead();

            Assert.Equal(expectedVal, val); // Returned value

            await SetErrorInjection(providerName, ErrorInjectionPoint.AfterRead);
            await CheckStorageProviderErrors(grain.DoRead);

            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value

            int newVal = 53;
            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", newVal);

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
            string providerName = ErrorInjectorProviderName;
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(id);

            var val = await grain.GetValue();

            int expectedVal = 62;
            await grain.DoWrite(expectedVal);
            var providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1

            const int attemptedVal3 = 63;
            await SetErrorInjection(providerName, ErrorInjectionPoint.BeforeWrite);
            await CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal3));

            // Stored value unchanged
            providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            val = await grain.GetValue();
            // Stored value unchanged
            providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1
#if REREAD_STATE_AFTER_WRITE_FAILED
            Assert.Equal(expectedVal, val); // Last value written successfully
#else
            Assert.Equal(attemptedVal3, val); // Last value attempted to be written is still in memory
#endif
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_InconsistentStateException_DeactivatesGrain()
        {
            Guid id = Guid.NewGuid();
            string providerName = ErrorInjectorProviderName;
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(id);

            var val = await grain.GetValue();

            int expectedVal = 62;
            var originalActivationId = await grain.GetActivationId();
            await grain.DoWrite(expectedVal);
            var providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1

            const int attemptedVal3 = 63;
            await SetErrorInjection(providerName, new ErrorInjectionBehavior
            {
                ErrorInjectionPoint = ErrorInjectionPoint.BeforeWrite,
                ExceptionType = typeof(InconsistentStateException)
            });
            await CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal3), typeof(InconsistentStateException));

            // Stored value unchanged
            providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1
            Assert.NotEqual(originalActivationId, await grain.GetActivationId());

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            val = await grain.GetValue();
            // Stored value unchanged
            providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1

            // The value should not have changed.
            Assert.Equal(expectedVal, val);
        }

        /// <summary>
        /// Tests that deactivations caused by an <see cref="InconsistentStateException"/> only affect the grain which
        /// the exception originated from.
        /// </summary>
        /// <returns></returns>
        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_InconsistentStateException_DeactivatesOnlyCurrentGrain()
        {
            var target = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(Guid.NewGuid());
            var proxy = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorProxyGrain>(Guid.NewGuid());
            
            // Record the original activation ids.
            var targetActivationId = await target.GetActivationId();
            var proxyActivationId = await proxy.GetActivationId();

            // Cause an inconsistent state exception.
            await SetErrorInjection(ErrorInjectorProviderName, new ErrorInjectionBehavior
            {
                ErrorInjectionPoint = ErrorInjectionPoint.BeforeWrite,
                ExceptionType = typeof(InconsistentStateException)
            });
            await CheckStorageProviderErrors(() => proxy.DoWrite(63, target), typeof(InconsistentStateException));

            // The target should have been deactivated by the exception.
            Assert.NotEqual(targetActivationId, await target.GetActivationId());

            // The grain which called the target grain should not have been deactivated.
            Assert.Equal(proxyActivationId, await proxy.GetActivationId());
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterWrite()
        {
            Guid id = Guid.NewGuid();
            string providerName = ErrorInjectorProviderName;
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(id);

            var val = await grain.GetValue();

            int expectedVal = 82;
            await grain.DoWrite(expectedVal);
            var providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1

            const int attemptedVal4 = 83;
            await SetErrorInjection(providerName, ErrorInjectionPoint.AfterWrite);
            await CheckStorageProviderErrors(() => grain.DoWrite(attemptedVal4));

            // Stored value has changed
            expectedVal = attemptedVal4;
            providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1
            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_BeforeReRead()
        {
            string providerName = ErrorInjectorProviderName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue();
            int expectedVal = 72;
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal);
            val = await grain.DoRead();
            Assert.Equal(expectedVal, val); // Returned value

            expectedVal = 73;
            await grain.DoWrite(expectedVal);
            var providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1

            await SetErrorInjection(providerName, ErrorInjectionPoint.BeforeRead);
            await CheckStorageProviderErrors(grain.DoRead);

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Error_AfterReRead()
        {
            string providerName = ErrorInjectorProviderName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceProviderErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceProviderErrorGrain>(guid);

            var val = await grain.GetValue();

            int expectedVal = 92;
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal);
            val = await grain.DoRead();
            Assert.Equal(expectedVal, val); // Returned value

            expectedVal = 93;
            await grain.DoWrite(expectedVal);
            var providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1

            expectedVal = 94;
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal);
            await SetErrorInjection(providerName, ErrorInjectionPoint.AfterRead);
            await CheckStorageProviderErrors(grain.DoRead);

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_Handled_Read()
        {
            string providerName = ErrorInjectorProviderName;
            Guid guid = Guid.NewGuid();
            _ = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);
            _ = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal);

            var val = await grain.DoRead(false);

            Assert.Equal(expectedVal, val); // Returned value

            int newVal = expectedVal + 1;

            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", newVal);
            await SetErrorInjection(providerName, ErrorInjectionPoint.BeforeRead);
            val = await grain.DoRead(true);
            Assert.Equal(expectedVal, val); // Returned value
            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            expectedVal = newVal;

            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", newVal);
            val = await grain.DoRead(false);
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_Handled_Write()
        {
            string providerName = ErrorInjectorProviderName;
            Guid guid = Guid.NewGuid();
            _ = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);
            _ = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal);

            var val = await grain.DoRead(false);

            Assert.Equal(expectedVal, val); // Returned value

            int newVal = expectedVal + 1;
            await SetErrorInjection(providerName, ErrorInjectionPoint.BeforeWrite);
            await grain.DoWrite(newVal, true);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);

            expectedVal = newVal;
            await grain.DoWrite(newVal, false);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence")]
        public async Task Persistence_Error_NotHandled_Write()
        {
            string providerName = ErrorInjectorProviderName;
            Guid guid = Guid.NewGuid();
            string id = guid.ToString("N");
            IPersistenceUserHandledErrorGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceUserHandledErrorGrain>(guid);

            var val = await grain.GetValue(); // Activate grain
            int expectedVal = 42;

            await SetErrorInjection(providerName, ErrorInjectionPoint.None);
            SetStoredValue(providerName, typeof(ErrorInjectionStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal);
            val = await grain.DoRead(false);

            Assert.Equal(expectedVal, val); // Returned value after read

            int newVal = expectedVal + 1;
            await SetErrorInjection(providerName, ErrorInjectionPoint.BeforeWrite);
            await CheckStorageProviderErrors(() => grain.DoWrite(newVal, false));

            val = await grain.GetValue();
            // Stored value unchanged
            var providerState = GetStateForStorageProviderInUse(providerName, typeof(ErrorInjectionStorageProvider).FullName);
            Assert.Equal(expectedVal, providerState.LastStoredGrainState.Field1); // Store-Field1
#if REREAD_STATE_AFTER_WRITE_FAILED
            Assert.Equal(expectedVal, val); // After failed write: Last value written successfully
#else
            Assert.Equal(newVal, val); // After failed write: Last value attempted to be written is still in memory
#endif
            await SetErrorInjection(providerName, ErrorInjectionPoint.None);

            expectedVal = newVal;
            await grain.DoWrite(newVal, false);
            val = await grain.GetValue();
            Assert.Equal(expectedVal, val); // Returned value after good write
        }

        [Fact, TestCategory("Stress"), TestCategory("CorePerf"), TestCategory("Persistence")]
        public async Task Persistence_Provider_Loop_Read()
        {
            const int numIterations = 100;

            Task<int>[] promises = new Task<int>[numIterations];
            for (int i = 0; i < numIterations; i++)
            {
                int expectedVal = i;
                IPersistenceTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceTestGrain>(Guid.NewGuid());
                Guid guid = grain.GetPrimaryKey();
                _ = guid.ToString("N");

                SetStoredValue(MockStorageProviderName1, typeof(MockStorageProvider).FullName, DefaultGrainStateName, grain, "Field1", expectedVal); // Update state data behind grain
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
            var oex = await Assert.ThrowsAsync<BadProviderConfigException>(() => grain.DoSomething());
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

            Assert.StartsWith(msg3,  ae.Message);  //  "AggregateException.Message should be '{0}'", msg3
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
            const string providerName = MockStorageProviderName1;
            Guid id = Guid.NewGuid();
            IPersistenceNoStateTestGrain grain = this.HostedCluster.GrainFactory.GetGrain<IPersistenceNoStateTestGrain>(id);

            await grain.DoSomething();

            Assert.True(HasStorageProvider(providerName));
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

            var copy = (GrainStateContainingGrainReferences)this.HostedCluster.DeepCopy(initialState);
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
                int idx = Random.Shared.Next(num);
                tasks.Add(Task.Run(() => { var copy = this.HostedCluster.DeepCopy(states[idx]); }));
                tasks.Add(Task.Run(() => { var other = this.HostedCluster.RoundTripSerializationForTesting(states[idx]); }));
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

        // ---------- Utility functions ----------
        private void SetStoredValue(string providerName, string providerTypeFullName, string grainType, IGrain grain, string fieldName, int newValue)
        {
            IManagementGrain mgmtGrain = this.HostedCluster.GrainFactory.GetGrain<IManagementGrain>(0);
            // set up SetVal func args
            var args = new MockStorageProvider.SetValueArgs
            {
                Val = newValue,
                Name = "Field1",
                GrainType = grainType,
                GrainId = grain.GetGrainId(),
                StateType = typeof(PersistenceTestGrainState)
            };
            mgmtGrain.SendControlCommandToProvider(providerTypeFullName,
                providerName, (int)MockStorageProvider.Commands.SetValue, args).Wait();
        }

        private async Task SetErrorInjection(string providerName, ErrorInjectionPoint errorInjectionPoint)
        {
            await SetErrorInjection(providerName, new ErrorInjectionBehavior { ErrorInjectionPoint = errorInjectionPoint });
        }

        private async Task SetErrorInjection(string providerName, ErrorInjectionBehavior errorInjectionBehavior)
        {
            await ErrorInjectionStorageProvider.SetErrorInjection(providerName, errorInjectionBehavior, this.HostedCluster.GrainFactory);
        }

        private async Task CheckStorageProviderErrors(Func<Task> taskFunc, Type expectedException = null)
        {
            StackTrace at = new StackTrace();
            TimeSpan timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(15);
            try
            {
                await taskFunc().WithTimeout(timeout);

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
                var baseException = e.GetBaseException();
                if (baseException is OrleansException && baseException.InnerException != null)
                {
                    baseException = baseException.InnerException;
                }

                Assert.IsAssignableFrom(expectedException ?? typeof(StorageProviderInjectedError), baseException);
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
        private bool HasStorageProvider(string providerName)
        {
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos())
            {
                if (this.HostedCluster.Client.GetTestHooks(siloHandle).HasStorageProvider(providerName).Result)
                {
                    return true;
                }
            }
            return false;
        }

        private ProviderState GetStateForStorageProviderInUse(string providerName, string providerTypeFullName, bool okNull = false)
        {
            ProviderState providerState = new ProviderState();
            IManagementGrain mgmtGrain = this.HostedCluster.GrainFactory.GetGrain<IManagementGrain>(0);
            object[] replies = mgmtGrain.SendControlCommandToProvider(providerTypeFullName,
               providerName, (int)MockStorageProvider.Commands.GetProvideState, null).Result;
            object[] replies2 = mgmtGrain.SendControlCommandToProvider(providerTypeFullName,
                              providerName, (int)MockStorageProvider.Commands.GetLastState, null).Result;
            for(int i = 0; i < replies.Length; i++)
            {
                MockStorageProvider.StateForTest state = (MockStorageProvider.StateForTest)replies[i];
                PersistenceTestGrainState grainState = (PersistenceTestGrainState)replies2[i];
                if (state.ReadCount > 0)
                {
                    providerState.ProviderStateForTest = state;
                    providerState.LastStoredGrainState = grainState;
                    return providerState;
                }
            }
            
            return providerState;
        }

        class ProviderState
        {
            public MockStorageProvider.StateForTest ProviderStateForTest { get; set; }
            public PersistenceTestGrainState LastStoredGrainState { get; set; }
        }

        private void ResetMockStorageProvidersHistory()
        {
            var mockStorageProviders = new[] { MockStorageProviderName1, MockStorageProviderName2, MockStorageProviderNameLowerCase };
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos().ToList())
            {
                foreach (var providerName in mockStorageProviders)
                {
                    if (!this.HostedCluster.Client.GetTestHooks(siloHandle).HasStorageProvider(providerName).Result) continue;
                    IManagementGrain mgmtGrain = this.HostedCluster.GrainFactory.GetGrain<IManagementGrain>(0);
                    _ = mgmtGrain.SendControlCommandToProvider(typeof(MockStorageProvider).FullName,
                       providerName, (int)MockStorageProvider.Commands.ResetHistory, null).Result;
                }
            }
        }
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

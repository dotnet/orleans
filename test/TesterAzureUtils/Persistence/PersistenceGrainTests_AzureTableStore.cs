//#define REREAD_STATE_AFTER_WRITE_FAILED


using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.Storage;
using Orleans.TestingHost;
using Tester;
using UnitTests;
using UnitTests.GrainInterfaces;
using UnitTests.StorageTests;
using Xunit;
using Xunit.Abstractions;
using Orleans.Runtime.Configuration;
using System.Collections.Generic;
using Orleans.Providers;
using System.Linq;
using TestExtensions;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace Tester.AzureUtils.Persistence
{
    /// <summary>
    /// PersistenceGrainTests using AzureTableStore - Requires access to external Azure table storage
    /// </summary>
    public class PersistenceGrainTests_AzureTableStore : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureTableStore.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                TestUtils.CheckForAzureStorage();

                Guid serviceId = Guid.NewGuid();
                var options = new TestClusterOptions(initialSilosCount: 4);

                options.ClusterConfiguration.Globals.ServiceId = serviceId;

                options.ClusterConfiguration.Globals.MaxResendCount = 0;

                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2", new Dictionary<string, string> { { "Config1", "1" }, { "Config2", "2" } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureTableStorage>("AzureStore", new Dictionary<string, string> { { "DeleteStateOnClear", "true" }, { "DataConnectionString", options.ClusterConfiguration.Globals.DataConnectionString } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureTableStorage>("AzureStore1", new Dictionary<string, string> { { "DataConnectionString", options.ClusterConfiguration.Globals.DataConnectionString } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureTableStorage>("AzureStore2", new Dictionary<string, string> { { "DataConnectionString", options.ClusterConfiguration.Globals.DataConnectionString } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureTableStorage>("AzureStore3", new Dictionary<string, string> { { "DataConnectionString", options.ClusterConfiguration.Globals.DataConnectionString } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.ShardedStorageProvider>("ShardedAzureStore");


                IProviderConfiguration providerConfig;
                if (options.ClusterConfiguration.Globals.TryGetProviderConfiguration("Orleans.Storage.ShardedStorageProvider", "ShardedAzureStore", out providerConfig))
                {
                    var providerCategoriess = options.ClusterConfiguration.Globals.ProviderConfigurations;

                    var providers = providerCategoriess.SelectMany(o => o.Value.Providers);

                    IProviderConfiguration provider1 = GetNamedProviderConfigForShardedProvider(providers, "AzureStore1");
                    IProviderConfiguration provider2 = GetNamedProviderConfigForShardedProvider(providers, "AzureStore2");
                    IProviderConfiguration provider3 = GetNamedProviderConfigForShardedProvider(providers, "AzureStore3");
                    providerConfig.AddChildConfiguration(provider1);
                    providerConfig.AddChildConfiguration(provider2);
                    providerConfig.AddChildConfiguration(provider3);
                }

                return new TestCluster(options);
            }
        }

        public PersistenceGrainTests_AzureTableStore(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureTableStore_Delete()
        {
            await base.Grain_AzureStore_Delete();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureTableStore_Read()
        {
            await base.Grain_AzureStore_Read();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKey_AzureTableStore_Read_Write()
        {
            await base.Grain_GuidKey_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKey_AzureTableStore_Read_Write()
        {
            await base.Grain_LongKey_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKeyExtended_AzureTableStore_Read_Write()
        {
            await base.Grain_LongKeyExtended_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKeyExtended_AzureTableStore_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureTableStore_Read_Write()
        {
            await base.Grain_Generic_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureTableStore_DiffTypes()
        {
            await base.Grain_Generic_AzureStore_DiffTypes();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureTableStore_SiloRestart()
        {
            await base.Grain_AzureStore_SiloRestart();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Activate_AzureTableStore()
        {
            base.Persistence_Perf_Activate();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write_AzureTableStore()
        {
            base.Persistence_Perf_Write();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread_AzureTableStore()
        {
            base.Persistence_Perf_Write_Reread();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public Task Persistence_Silo_StorageProvider_AzureTableStore()
        {
            return base.Persistence_Silo_StorageProvider_Azure(typeof(AzureTableStorage));
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureTableStore_ConvertToFromStorageFormat_GrainReference()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid id = Guid.NewGuid();
            IUser grain = this.GrainFactory.GetGrain<IUser>(id);

            var initialState = new GrainStateContainingGrainReferences { Grain = grain };
            var entity = new DynamicTableEntity();
            var storage = new AzureTableStorage();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            var convertedState = new GrainStateContainingGrainReferences();
            convertedState = (GrainStateContainingGrainReferences)storage.ConvertFromStorageFormat(entity);
            Assert.NotNull(convertedState); // Converted state
            Assert.Equal(initialState.Grain,  convertedState.Grain);  // "Grain"
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureTableStore_ConvertToFromStorageFormat_GrainReference_List()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid[] ids = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            IUser[] grains = new IUser[3];
            grains[0] = this.GrainFactory.GetGrain<IUser>(ids[0]);
            grains[1] = this.GrainFactory.GetGrain<IUser>(ids[1]);
            grains[2] = this.GrainFactory.GetGrain<IUser>(ids[2]);

            var initialState = new GrainStateContainingGrainReferences();
            foreach (var g in grains)
            {
                initialState.GrainList.Add(g);
                initialState.GrainDict.Add(g.GetPrimaryKey().ToString(), g);
            }
            var entity = new DynamicTableEntity();
            var storage = new AzureTableStorage();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            var convertedState = (GrainStateContainingGrainReferences)storage.ConvertFromStorageFormat(entity);
            Assert.NotNull(convertedState);
            Assert.Equal(initialState.GrainList.Count,  convertedState.GrainList.Count);  // "GrainList size"
            Assert.Equal(initialState.GrainDict.Count,  convertedState.GrainDict.Count);  // "GrainDict size"
            for (int i = 0; i < grains.Length; i++)
            {
                string iStr = ids[i].ToString();
                Assert.Equal(initialState.GrainList[i],  convertedState.GrainList[i]);  // "GrainList #{0}", i
                Assert.Equal(initialState.GrainDict[iStr],  convertedState.GrainDict[iStr]);  // "GrainDict #{0}", i
            }
            Assert.Equal(initialState.Grain,  convertedState.Grain);  // "Grain"
        }
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming
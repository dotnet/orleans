//#define REREAD_STATE_AFTER_WRITE_FAILED


using System;
using System.IO;
using System.Threading.Tasks;
using Orleans.Storage;
using Orleans.TestingHost;
using Tester;
using UnitTests;
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
    /// PersistenceGrainTests using AzureStore - Requires access to external Azure blob storage
    /// </summary>
    public class PersistenceGrainTests_AzureBlobStore : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureBlobStore.Fixture>
    {
        public class Fixture : BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                TestUtils.CheckForAzureStorage();

                Guid serviceId = Guid.NewGuid();
                var options = new TestClusterOptions(initialSilosCount: 1);

                options.ClusterConfiguration.Globals.ServiceId = serviceId;

                options.ClusterConfiguration.ApplyToAllNodes(n => n.MaxActiveThreads = 0);
                options.ClusterConfiguration.Globals.MaxResendCount = 0;

                //options.ClusterConfiguration.Globals.DataConnectionString = TestDefaultConfiguration.DataConnectionString;
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2", new Dictionary<string, string> { { "Config1", "1" }, { "Config2", "2" } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureBlobStorage>("AzureStore", new Dictionary<string, string> { { "DeleteStateOnClear", "true" } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureBlobStorage>("AzureStore1");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureBlobStorage>("AzureStore2");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureBlobStorage>("AzureStore3");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.ShardedStorageProvider>("ShardedAzureStore");

                //< Provider Name = "AzureStore1" />
                //< Provider Name = "AzureStore2" />
                //< Provider Name = "AzureStore3" />
                IProviderConfiguration providerConfig;
                if (options.ClusterConfiguration.Globals.TryGetProviderConfiguration("Orleans.Storage.ShardedStorageProvider", "ShardedAzureStore", out providerConfig))
                {
                    var providerCategoriess = options.ClusterConfiguration.Globals.ProviderConfigurations;

                    var providers = providerCategoriess.SelectMany(o => o.Value.Providers);

                    IProvider provider1 = GetNamedProviderForShardedProvider(providers, "AzureStore1");
                    IProvider provider2 = GetNamedProviderForShardedProvider(providers, "AzureStore2");
                    IProvider provider3 = GetNamedProviderForShardedProvider(providers, "AzureStore3");
                    providerConfig.Children.Add(provider1);
                    providerConfig.Children.Add(provider2);
                    providerConfig.Children.Add(provider3);
                }

                return new TestCluster(options);
            }

            private static IProvider GetNamedProviderForShardedProvider(IEnumerable<KeyValuePair<string, IProviderConfiguration>> providers, string providerName)
            {
                var ddbStore1 = providers.Where(o => o.Key.Equals(providerName)).Select(o => o.Value);

                var pm = ddbStore1.FirstOrDefault();

                var provider = ((ProviderConfiguration) pm).ProviderManager.GetProvider(providerName);
                return provider;
            }


        }

        public PersistenceGrainTests_AzureBlobStore(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
        }
        
        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureBlobStore_Delete()
        {
            await base.Grain_AzureStore_Delete();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureBlobStore_Read()
        {
            await base.Grain_AzureStore_Read();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKey_AzureBlobStore_Read_Write()
        {
            await base.Grain_GuidKey_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKey_AzureBlobStore_Read_Write()
        {
            await base.Grain_LongKey_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKeyExtended_AzureBlobStore_Read_Write()
        {
            await base.Grain_LongKeyExtended_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKeyExtended_AzureBlobStore_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureBlobStore_Read_Write()
        {
            await base.Grain_Generic_AzureStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureBlobStore_DiffTypes()
        {
            await base.Grain_Generic_AzureStore_DiffTypes();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureBlobStore_SiloRestart()
        {
            await base.Grain_AzureStore_SiloRestart();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Activate_AzureBlobStore()
        {
            base.Persistence_Perf_Activate();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write_AzureBlobStore()
        {
            base.Persistence_Perf_Write();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread_AzureBlobStore()
        {
            base.Persistence_Perf_Write_Reread();
        }

      
        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public Task Persistence_Silo_StorageProvider_AzureBlobStore()
        {
            return base.Persistence_Silo_StorageProvider_Azure(typeof(AzureBlobStorage));
        }

    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

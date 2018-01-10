﻿//#define REREAD_STATE_AFTER_WRITE_FAILED


using System;
using System.Threading.Tasks;
using Orleans.Storage;
using Orleans.TestingHost;
using Xunit;
using Xunit.Abstractions;
using Orleans.Runtime.Configuration;
using System.Collections.Generic;
using TestExtensions;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace Tester.AzureUtils.Persistence
{
    /// <summary>
    /// PersistenceGrainTests using AzureStore - Requires access to external Azure blob storage
    /// </summary>
    [TestCategory("Persistence"), TestCategory("Azure")]
    public class PersistenceGrainTests_AzureBlobStore : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureBlobStore.Fixture>
    {
        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                Guid serviceId = Guid.NewGuid();
                var options = new TestClusterOptions(initialSilosCount: 4);
                options.ClusterConfiguration.Globals.DataConnectionString = TestDefaultConfiguration.DataConnectionString;

                options.ClusterConfiguration.Globals.ServiceId = serviceId;

                options.ClusterConfiguration.Globals.MaxResendCount = 0;
                
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2", new Dictionary<string, string> { { "Config1", "1" }, { "Config2", "2" } });
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");

                options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                options.ClusterConfiguration.AddAzureBlobStorageProvider("AzureStore", options.ClusterConfiguration.Globals.DataConnectionString);
                options.ClusterConfiguration.AddAzureBlobStorageProvider("AzureStore1", options.ClusterConfiguration.Globals.DataConnectionString);
                options.ClusterConfiguration.AddAzureBlobStorageProvider("AzureStore2", options.ClusterConfiguration.Globals.DataConnectionString);
                options.ClusterConfiguration.AddAzureBlobStorageProvider("AzureStore3", options.ClusterConfiguration.Globals.DataConnectionString);

                return new TestCluster(options).UseSiloBuilderFactory<SiloBuilderFactory>().UseClientBuilderFactory(ClientBuilderFactory);
            }
        }

        public PersistenceGrainTests_AzureBlobStore(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            fixture.EnsurePreconditionsMet();
        }
        
        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureBlobStore_Delete()
        {
            await base.Grain_AzureStore_Delete();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureBlobStore_Read()
        {
            await base.Grain_AzureStore_Read();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKey_AzureBlobStore_Read_Write()
        {
            await base.Grain_GuidKey_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKey_AzureBlobStore_Read_Write()
        {
            await base.Grain_LongKey_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKeyExtended_AzureBlobStore_Read_Write()
        {
            await base.Grain_LongKeyExtended_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKeyExtended_AzureBlobStore_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_Generic_AzureBlobStore_Read_Write()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();

            await base.Grain_Generic_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_Generic_AzureBlobStore_DiffTypes()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();

            await base.Grain_Generic_AzureStore_DiffTypes();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureBlobStore_SiloRestart()
        {
            await base.Grain_AzureStore_SiloRestart();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Activate_AzureBlobStore()
        {
            base.Persistence_Perf_Activate();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Write_AzureBlobStore()
        {
            base.Persistence_Perf_Write();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread_AzureBlobStore()
        {
            base.Persistence_Perf_Write_Reread();
        }

      
        [SkippableTheory, TestCategory("Functional")]
        [InlineData("AzureStore")]
        [InlineData("AzureStore1")]
        [InlineData("AzureStore2")]
        [InlineData("AzureStore3")]
        public Task Persistence_Silo_StorageProvider_AzureBlobStore(string providerName)
        {
            return base.Persistence_Silo_StorageProvider_Azure(providerName);
        }

    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

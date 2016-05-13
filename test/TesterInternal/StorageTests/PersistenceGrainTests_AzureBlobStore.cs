//#define REREAD_STATE_AFTER_WRITE_FAILED

using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.Storage;
using Orleans.TestingHost;
using System;
using System.IO;
using System.Threading.Tasks;
using Tester;
using Xunit;
using Xunit.Abstractions;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace UnitTests.StorageTests
{
    /// <summary>
    /// PersistenceGrainTests using AzureStore - Requires access to external Azure blob storage
    /// </summary>
    public class PersistenceGrainTests_AzureBlobStore : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureBlobStore.Fixture>
    {
        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                TestUtils.CheckForAzureStorage();

                Guid serviceId = Guid.NewGuid();
                return new TestingSiloHost(new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("Config_AzureBlobStorage.xml"),
                    StartSecondary = false,
                    AdjustConfig = config =>
                    {
                        config.Globals.ServiceId = serviceId;
                    }
                });
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
        public void Persistence_Silo_StorageProvider_AzureBlobStore()
        {
            base.Persistence_Silo_StorageProvider_Azure(typeof(AzureBlobStorage));
        }

    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

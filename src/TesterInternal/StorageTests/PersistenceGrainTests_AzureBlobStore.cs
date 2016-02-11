//#define REREAD_STATE_AFTER_WRITE_FAILED

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Storage;
using Orleans.TestingHost;
using System;
using System.IO;
using System.Threading.Tasks;
using Tester;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace UnitTests.StorageTests
{
    /// <summary>
    /// PersistenceGrainTests using AzureStore - Requires access to external Azure blob storage
    /// </summary>
    [TestClass]
    [DeploymentItem("Config_AzureBlobStorage.xml")]
    public class PersistenceGrainTests_AzureBlobStore : Base_PersistenceGrainTests_AzureStore
    {

        private static Guid serviceId = Guid.NewGuid();

        private static readonly TestingSiloOptions testSiloOptions = new TestingSiloOptions
        {
            SiloConfigFile = new FileInfo("Config_AzureBlobStorage.xml"),
            StartFreshOrleans = true,
            StartPrimary = true,
            StartSecondary = false,
            AdjustConfig = config =>
            {
                config.Globals.ServiceId = serviceId;
            }
        };

        public static TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(testSiloOptions);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureBlobStore_Delete()
        {
            await base.Grain_AzureStore_Delete();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureBlobStore_Read()
        {
            await base.Grain_AzureStore_Read();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKey_AzureBlobStore_Read_Write()
        {
            await base.Grain_GuidKey_AzureStore_Read_Write();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKey_AzureBlobStore_Read_Write()
        {
            await base.Grain_LongKey_AzureStore_Read_Write();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_LongKeyExtended_AzureBlobStore_Read_Write()
        {
            await base.Grain_LongKeyExtended_AzureStore_Read_Write();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_GuidKeyExtended_AzureBlobStore_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AzureStore_Read_Write();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureBlobStore_Read_Write()
        {
            await base.Grain_Generic_AzureStore_Read_Write();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_Generic_AzureBlobStore_DiffTypes()
        {
            await base.Grain_Generic_AzureStore_DiffTypes();
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public async Task Grain_AzureBlobStore_SiloRestart()
        {
            await base.Grain_AzureStore_SiloRestart();
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Activate_AzureBlobStore()
        {
            base.Persistence_Perf_Activate();
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write_AzureBlobStore()
        {
            base.Persistence_Perf_Write();
        }

        [TestMethod, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("Azure"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread_AzureBlobStore()
        {
            base.Persistence_Perf_Write_Reread();
        }

      
        [TestMethod, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void Persistence_Silo_StorageProvider_AzureBlobStore()
        {
            base.Persistence_Silo_StorageProvider_Azure(typeof(AzureBlobStorage));
        }

    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming

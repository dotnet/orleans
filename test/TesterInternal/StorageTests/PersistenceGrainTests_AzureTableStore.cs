//#define REREAD_STATE_AFTER_WRITE_FAILED

using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.Storage;
using Orleans.TestingHost;
using System;
using System.IO;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using Xunit;
using Tester;
using Xunit.Abstractions;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace UnitTests.StorageTests
{
    /// <summary>
    /// PersistenceGrainTests using AzureTableStore - Requires access to external Azure table storage
    /// </summary>
    public class PersistenceGrainTests_AzureTableStore : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureTableStore.Fixture>
    {
        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                TestUtils.CheckForAzureStorage();

                Guid serviceId = Guid.NewGuid();
                return new TestingSiloHost(new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("Config_AzureTableStorage.xml"),
                    StartPrimary = true,
                    StartSecondary = false,
                    AdjustConfig = config =>
                    {
                        config.Globals.ServiceId = serviceId;
                    }
                });
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
        public void Persistence_Silo_StorageProvider_AzureTableStore()
        {
            base.Persistence_Silo_StorageProvider_Azure(typeof(AzureTableStorage));
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureTableStore_ConvertToFromStorageFormat_GrainReference()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid id = Guid.NewGuid();
            IUser grain = GrainClient.GrainFactory.GetGrain<IUser>(id);

            var initialState = new GrainStateContainingGrainReferences { Grain = grain };
            var entity = new DynamicTableEntity();
            var storage = new AzureTableStorage();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            var convertedState = new GrainStateContainingGrainReferences();
            convertedState = (GrainStateContainingGrainReferences)storage.ConvertFromStorageFormat(entity);
            Assert.IsNotNull(convertedState, "Converted state");
            Assert.AreEqual(initialState.Grain, convertedState.Grain, "Grain");
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("Azure")]
        public void AzureTableStore_ConvertToFromStorageFormat_GrainReference_List()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid[] ids = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
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
            var entity = new DynamicTableEntity();
            var storage = new AzureTableStorage();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
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
    }
}

// ReSharper restore RedundantAssignment
// ReSharper restore UnusedVariable
// ReSharper restore InconsistentNaming
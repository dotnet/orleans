using Orleans;
using Orleans.Storage;
using Orleans.TestingHost;
using System;
using System.IO;
using System.Threading.Tasks;
using UnitTests.GrainInterfaces;
using UnitTests.StorageTests.AWSUtils;
using Xunit;
using Xunit.Abstractions;
using static Orleans.Storage.DynamoDBStorageProvider;

namespace UnitTests.StorageTests
{
    public class PersistenceGrainTests_AWSDynamoDBStore : Base_PersistenceGrainTests_AWSStore, IClassFixture<PersistenceGrainTests_AWSDynamoDBStore.Fixture>
    {
        public class Fixture : BaseClusterFixture
        {
            protected override TestingSiloHost CreateClusterHost()
            {
                Guid serviceId = Guid.NewGuid();
                string dataConnectionString = $"Service={AWSTestConstants.Service}";
                return new TestingSiloHost(new TestingSiloOptions
                {
                    SiloConfigFile = new FileInfo("Config_AWS_DynamoDB_Storage.xml"),
                    StartPrimary = true,
                    StartSecondary = false,
                    AdjustConfig = config =>
                    {
                        config.Globals.ServiceId = serviceId;
                        config.Globals.DataConnectionString = dataConnectionString;
                    }
                });
            }
        }

        public PersistenceGrainTests_AWSDynamoDBStore(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_AWSDynamoDBStore_Delete()
        {
            await base.Grain_AWSStore_Delete();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_AWSDynamoDBStore_Read()
        {
            await base.Grain_AWSStore_Read();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_GuidKey_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_GuidKey_AWSStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_LongKey_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_LongKey_AWSStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_LongKeyExtended_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_LongKeyExtended_AWSStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_GuidKeyExtended_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AWSStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_Generic_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_Generic_AWSStore_Read_Write();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_Generic_AWSDynamoDBStore_DiffTypes()
        {
            await base.Grain_Generic_AWSStore_DiffTypes();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public async Task Grain_AWSDynamoDBStore_SiloRestart()
        {
            await base.Grain_AWSStore_SiloRestart();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("AWS"), TestCategory("Stress")]
        public void Persistence_Perf_Activate_AWSDynamoDBStore()
        {
            base.Persistence_Perf_Activate();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("AWS"), TestCategory("Stress")]
        public void Persistence_Perf_Write_AWSDynamoDBStore()
        {
            base.Persistence_Perf_Write();
        }

        [Fact, TestCategory("CorePerf"), TestCategory("Persistence"), TestCategory("Performance"), TestCategory("AWS"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread_AWSDynamoDBStore()
        {
            base.Persistence_Perf_Write_Reread();
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public void Persistence_Silo_StorageProvider_AWSDynamoDBStore()
        {
            base.Persistence_Silo_StorageProvider_AWS(typeof(DynamoDBStorageProvider));
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public void AWSDynamoDBStore_ConvertToFromStorageFormat_GrainReference()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid id = Guid.NewGuid();
            IUser grain = GrainClient.GrainFactory.GetGrain<IUser>(id);

            var initialState = new GrainStateContainingGrainReferences { Grain = grain };
            var entity = new GrainStateRecord();
            var storage = new DynamoDBStorageProvider();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            var convertedState = new GrainStateContainingGrainReferences();
            convertedState = (GrainStateContainingGrainReferences)storage.ConvertFromStorageFormat(entity);
            Assert.NotNull(convertedState); // Converted state
            Assert.Equal(initialState.Grain, convertedState.Grain);  // "Grain"
        }

        [Fact, TestCategory("Functional"), TestCategory("Persistence"), TestCategory("AWS")]
        public void AWSDynamoDBStore_ConvertToFromStorageFormat_GrainReference_List()
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
            var entity = new GrainStateRecord();
            var storage = new DynamoDBStorageProvider();
            storage.InitLogger(logger);
            storage.ConvertToStorageFormat(initialState, entity);
            var convertedState = (GrainStateContainingGrainReferences)storage.ConvertFromStorageFormat(entity);
            Assert.NotNull(convertedState);
            Assert.Equal(initialState.GrainList.Count, convertedState.GrainList.Count);  // "GrainList size"
            Assert.Equal(initialState.GrainDict.Count, convertedState.GrainDict.Count);  // "GrainDict size"
            for (int i = 0; i < grains.Length; i++)
            {
                string iStr = ids[i].ToString();
                Assert.Equal(initialState.GrainList[i], convertedState.GrainList[i]);  // "GrainList #{0}", i
                Assert.Equal(initialState.GrainDict[iStr], convertedState.GrainDict[iStr]);  // "GrainDict #{0}", i
            }
            Assert.Equal(initialState.Grain, convertedState.Grain);  // "Grain"
        }
    }
}

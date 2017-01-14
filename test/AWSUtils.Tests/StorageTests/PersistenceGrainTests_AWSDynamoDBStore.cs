using Orleans;
using Orleans.Providers;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Storage;
using Orleans.Storage;
using Orleans.TestingHost;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnitTests;
using UnitTests.GrainInterfaces;
using UnitTests.StorageTests;
using Xunit;
using Xunit.Abstractions;
using static Orleans.Storage.DynamoDBStorageProvider;

namespace AWSUtils.Tests.StorageTests
{
    [TestCategory("Persistence"), TestCategory("AWS"), TestCategory("DynamoDb")]
    public class PersistenceGrainTests_AWSDynamoDBStore : Base_PersistenceGrainTests_AWSStore, IClassFixture<PersistenceGrainTests_AWSDynamoDBStore.Fixture>
    {
        public class Fixture : TestExtensions.BaseTestClusterFixture
        {
            protected override TestCluster CreateTestCluster()
            {
                if (AWSTestConstants.IsDynamoDbAvailable)
                {
                    Guid serviceId = Guid.NewGuid();
                    string dataConnectionString = $"Service={AWSTestConstants.Service}";
                    var options = new TestClusterOptions(initialSilosCount: 4);


                    options.ClusterConfiguration.Globals.ServiceId = serviceId;
                    options.ClusterConfiguration.Globals.DataConnectionString = dataConnectionString;

                    options.ClusterConfiguration.Globals.MaxResendCount = 0;


                    options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                    options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2", new Dictionary<string, string> { { "Config1", "1" }, { "Config2", "2" } });
                    options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                    options.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");

                    options.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                    options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.DynamoDBStorageProvider>("DDBStore", new Dictionary<string, string> { { "DeleteStateOnClear", "true" }, { "DataConnectionString", dataConnectionString } });
                    options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.DynamoDBStorageProvider>("DDBStore1", new Dictionary<string, string> { { "DataConnectionString", dataConnectionString } });
                    options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.DynamoDBStorageProvider>("DDBStore2", new Dictionary<string, string> { { "DataConnectionString", dataConnectionString } });
                    options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.DynamoDBStorageProvider>("DDBStore3", new Dictionary<string, string> { { "DataConnectionString", dataConnectionString } });
                    options.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.ShardedStorageProvider>("ShardedDDBStore");

                    IProviderConfiguration providerConfig;
                    if(options.ClusterConfiguration.Globals.TryGetProviderConfiguration("Orleans.Storage.ShardedStorageProvider", "ShardedDDBStore", out providerConfig))
                    {
                        var providerCategoriess = options.ClusterConfiguration.Globals.ProviderConfigurations;

                        var providers = providerCategoriess.SelectMany(o => o.Value.Providers);

                        IProviderConfiguration provider1 = GetNamedProviderConfigForShardedProvider(providers, "DDBStore1");
                        IProviderConfiguration provider2 = GetNamedProviderConfigForShardedProvider(providers, "DDBStore2");
                        IProviderConfiguration provider3 = GetNamedProviderConfigForShardedProvider(providers, "DDBStore3");
                        providerConfig.AddChildConfiguration(provider1);
                        providerConfig.AddChildConfiguration(provider2);
                        providerConfig.AddChildConfiguration(provider3);
                    }
                    return new TestCluster(options);
                }
                return null;
            }

            private static IProviderConfiguration GetNamedProviderConfigForShardedProvider(IEnumerable<KeyValuePair<string, IProviderConfiguration>> providers, string providerName)
            {
                var providerConfig = providers.Where(o => o.Key.Equals(providerName)).Select(o => o.Value);

                return providerConfig.First();
            }
        }

        public PersistenceGrainTests_AWSDynamoDBStore(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            if (!AWSTestConstants.IsDynamoDbAvailable)
            {
                output.WriteLine("Unable to connect to AWS DynamoDB simulator");
                throw new SkipException("Unable to connect to AWS DynamoDB simulator");
            }
            
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AWSDynamoDBStore_Delete()
        {
            await base.Grain_AWSStore_Delete();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AWSDynamoDBStore_Read()
        {
            await base.Grain_AWSStore_Read();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKey_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_GuidKey_AWSStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKey_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_LongKey_AWSStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKeyExtended_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_LongKeyExtended_AWSStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKeyExtended_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AWSStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_Generic_AWSDynamoDBStore_Read_Write()
        {
            await base.Grain_Generic_AWSStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_Generic_AWSDynamoDBStore_DiffTypes()
        {
            await base.Grain_Generic_AWSStore_DiffTypes();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AWSDynamoDBStore_SiloRestart()
        {
            await base.Grain_AWSStore_SiloRestart();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Activate_AWSDynamoDBStore()
        {
            base.Persistence_Perf_Activate();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Write_AWSDynamoDBStore()
        {
            base.Persistence_Perf_Write();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread_AWSDynamoDBStore()
        {
            base.Persistence_Perf_Write_Reread();
        }

        [SkippableFact, TestCategory("Functional")]
        public Task Persistence_Silo_StorageProvider_AWSDynamoDBStore()
        {
            return base.Persistence_Silo_StorageProvider_AWS(typeof(DynamoDBStorageProvider));
        }

        [SkippableFact, TestCategory("Functional")]
        public void AWSDynamoDBStore_ConvertToFromStorageFormat_GrainReference()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid id = Guid.NewGuid();
            IUser grain = this.HostedCluster.GrainFactory.GetGrain<IUser>(id);

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

        [SkippableFact, TestCategory("Functional")]
        public void AWSDynamoDBStore_ConvertToFromStorageFormat_GrainReference_List()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid[] ids = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            IUser[] grains = new IUser[3];
            grains[0] = this.HostedCluster.GrainFactory.GetGrain<IUser>(ids[0]);
            grains[1] = this.HostedCluster.GrainFactory.GetGrain<IUser>(ids[1]);
            grains[2] = this.HostedCluster.GrainFactory.GetGrain<IUser>(ids[2]);

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

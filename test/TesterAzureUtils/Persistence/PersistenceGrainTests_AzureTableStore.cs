//#define REREAD_STATE_AFTER_WRITE_FAILED


using System;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.Storage;
using Orleans.TestingHost;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;
using Orleans.Runtime.Configuration;
using System.Collections.Generic;
using System.Linq;
using Orleans.Providers;
using Microsoft.Extensions.DependencyInjection;
using TestExtensions;
using TesterInternal;

// ReSharper disable RedundantAssignment
// ReSharper disable UnusedVariable
// ReSharper disable InconsistentNaming

namespace Tester.AzureUtils.Persistence
{
    /// <summary>
    /// PersistenceGrainTests using AzureTableStore - Requires access to external Azure table storage
    /// </summary>
    [TestCategory("Persistence"), TestCategory("Azure")]
    public class PersistenceGrainTests_AzureTableStore : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureTableStore.Fixture>
    {
        private readonly Dictionary<string, string> providerProperties = new Dictionary<string, string>
        {
            {"DataConnectionString", TestDefaultConfiguration.DataConnectionString}
        };

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                Guid serviceId = Guid.NewGuid();
                builder.Options.InitialSilosCount = 4;
                builder.Options.UseTestClusterMembership = false;
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.DataConnectionString = TestDefaultConfiguration.DataConnectionString;

                    legacy.ClusterConfiguration.Globals.ServiceId = serviceId;

                    legacy.ClusterConfiguration.Globals.MaxResendCount = 0;

                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2",
                        new Dictionary<string, string> {{"Config1", "1"}, {"Config2", "2"}});
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");

                    legacy.ClusterConfiguration.AddMemoryStorageProvider("MemoryStore");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureTableStorage>("AzureStore",
                        new Dictionary<string, string> {{"DeleteStateOnClear", "true"}, {"DataConnectionString", legacy.ClusterConfiguration.Globals.DataConnectionString}});
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureTableStorage>("AzureStore1",
                        new Dictionary<string, string> {{"DataConnectionString", legacy.ClusterConfiguration.Globals.DataConnectionString}});
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureTableStorage>("AzureStore2",
                        new Dictionary<string, string> {{"DataConnectionString", legacy.ClusterConfiguration.Globals.DataConnectionString}});
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<Orleans.Storage.AzureTableStorage>("AzureStore3",
                        new Dictionary<string, string> {{"DataConnectionString", legacy.ClusterConfiguration.Globals.DataConnectionString}});
                });
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
            }
        }

        public PersistenceGrainTests_AzureTableStore(ITestOutputHelper output, Fixture fixture) : base(output, fixture)
        {
            fixture.EnsurePreconditionsMet();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableStore_Delete()
        {
            await base.Grain_AzureStore_Delete();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableStore_Read()
        {
            await base.Grain_AzureStore_Read();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKey_AzureTableStore_Read_Write()
        {
            await base.Grain_GuidKey_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKey_AzureTableStore_Read_Write()
        {
            await base.Grain_LongKey_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKeyExtended_AzureTableStore_Read_Write()
        {
            await base.Grain_LongKeyExtended_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKeyExtended_AzureTableStore_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_Generic_AzureTableStore_Read_Write()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();

            await base.Grain_Generic_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_Generic_AzureTableStore_DiffTypes()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();

            await base.Grain_Generic_AzureStore_DiffTypes();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableStore_SiloRestart()
        {
            await base.Grain_AzureStore_SiloRestart();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Activate_AzureTableStore()
        {
            base.Persistence_Perf_Activate();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Write_AzureTableStore()
        {
            base.Persistence_Perf_Write();
        }

        [SkippableFact, TestCategory("CorePerf"), TestCategory("Performance"), TestCategory("Stress")]
        public void Persistence_Perf_Write_Reread_AzureTableStore()
        {
            base.Persistence_Perf_Write_Reread();
        }

        [SkippableTheory, TestCategory("Functional")]
        [InlineData("AzureStore")]
        [InlineData("AzureStore1")]
        [InlineData("AzureStore2")]
        [InlineData("AzureStore3")]
        public Task Persistence_Silo_StorageProvider_AzureTableStore(string providerName)
        {
            return base.Persistence_Silo_StorageProvider_Azure(providerName);
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableStore_ConvertToFromStorageFormat_GrainReference()
        {
            // NOTE: This test requires Silo to be running & Client init so that grain references can be resolved before serialization.
            Guid id = Guid.NewGuid();
            IUser grain = this.GrainFactory.GetGrain<IUser>(id);

            var initialState = new GrainStateContainingGrainReferences { Grain = grain };
            var entity = new DynamicTableEntity();
            var storage = new AzureTableStorage();
            await storage.Init("AzStore", this.HostedCluster.ServiceProvider.GetRequiredService<ClientProviderRuntime>(), new ProviderConfiguration(this.providerProperties));
            storage.ConvertToStorageFormat(initialState, entity);
            var convertedState = new GrainStateContainingGrainReferences();
            convertedState = (GrainStateContainingGrainReferences)storage.ConvertFromStorageFormat(entity);
            Assert.NotNull(convertedState); // Converted state
            Assert.Equal(initialState.Grain,  convertedState.Grain);  // "Grain"
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task AzureTableStore_ConvertToFromStorageFormat_GrainReference_List()
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
            await storage.Init("AzStore", this.HostedCluster.ServiceProvider.GetRequiredService<ClientProviderRuntime>(), new ProviderConfiguration(this.providerProperties));
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
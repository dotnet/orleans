//#define REREAD_STATE_AFTER_WRITE_FAILED

using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Orleans.TestingHost;
using Orleans.Hosting;
using Orleans.Configuration;

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
            private class StorageSiloBuilderConfigurator : ISiloConfigurator
            {
                public void Configure(ISiloBuilder hostBuilder)
                {
                    hostBuilder.AddAzureBlobGrainStorage("AzureStore", (AzureBlobStorageOptions options) =>
                    {
                        options.ConfigureTestDefaults();
                    })
                    .AddAzureBlobGrainStorage("AzureStore1", (AzureBlobStorageOptions options) =>
                    {
                        options.ConfigureTestDefaults();
                    })
                    .AddAzureBlobGrainStorage("AzureStore2", (AzureBlobStorageOptions options) =>
                    {
                        options.ConfigureTestDefaults();
                    })
                    .AddAzureBlobGrainStorage("AzureStore3", (AzureBlobStorageOptions options) =>
                    {
                        options.ConfigureTestDefaults();
                    })
                    .AddAzureBlobGrainStorage("GrainStorageForTest", (AzureBlobStorageOptions options) =>
                    {
                        options.ConfigureTestDefaults();
                    })
                    .AddMemoryGrainStorage("MemoryStore");
                }
            }

            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 4;
                builder.Options.UseTestClusterMembership = false;
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
                builder.AddSiloBuilderConfigurator<StorageSiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
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

        [SkippableFact(Skip="https://github.com/dotnet/orleans/issues/5651"), TestCategory("Functional")]
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

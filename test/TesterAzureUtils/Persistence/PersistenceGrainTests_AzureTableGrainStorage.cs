using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Xunit;
using Xunit.Abstractions;
using Orleans.Runtime.Configuration;
using Orleans.Hosting;
using Orleans.TestingHost;
using TestExtensions;

namespace Tester.AzureUtils.Persistence
{
    /// <summary>
    /// PersistenceGrainTests using AzureGrainStorage - Requires access to external Azure table storage
    /// </summary>
    [TestCategory("Persistence"), TestCategory("Azure")]
    public class PersistenceGrainTests_AzureTableGrainStorage : Base_PersistenceGrainTests_AzureStore, IClassFixture<PersistenceGrainTests_AzureTableGrainStorage.Fixture>
    {
        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.Options.InitialSilosCount = 4;
                builder.Options.UseTestClusterMembership = false;
                builder.ConfigureLegacyConfiguration(legacy =>
                {
                    legacy.ClusterConfiguration.Globals.ServiceId = Guid.NewGuid();
                    legacy.ClusterConfiguration.Globals.DataConnectionString = TestDefaultConfiguration.DataConnectionString;

                    legacy.ClusterConfiguration.Globals.MaxResendCount = 0;

                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test1");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("test2",
                        new Dictionary<string, string> { { "Config1", "1" }, { "Config2", "2" } });
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.ErrorInjectionStorageProvider>("ErrorInjector");
                    legacy.ClusterConfiguration.Globals.RegisterStorageProvider<UnitTests.StorageTests.MockStorageProvider>("lowercase");
                    
                });
                builder.AddSiloBuilderConfigurator<SiloBuilderConfigurator>();
                builder.AddSiloBuilderConfigurator<MySiloBuilderConfigurator>();
                builder.AddClientBuilderConfigurator<ClientBuilderConfigurator>();
            }

            private class MySiloBuilderConfigurator : ISiloBuilderConfigurator
            {
                public void Configure(ISiloHostBuilder hostBuilder)
                {
                    hostBuilder
                        .AddAzureTableGrainStorage("GrainStorageForTest", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                            options.DeleteStateOnClear = true;
                        }))
                        .AddAzureTableGrainStorage("AzureStore1", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        }))
                        .AddAzureTableGrainStorage("AzureStore2", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        }))
                        .AddAzureTableGrainStorage("AzureStore3", builder => builder.Configure<IOptions<ClusterOptions>>((options, silo) =>
                        {
                            options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
                        }))
                        .AddMemoryGrainStorage("MemoryStore");
                }
            }
        }

        public PersistenceGrainTests_AzureTableGrainStorage(ITestOutputHelper output, Fixture fixture) : 
            base(output, fixture)
        {
            fixture.EnsurePreconditionsMet();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableGrainStorage_Delete()
        {
            await base.Grain_AzureStore_Delete();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableGrainStorage_Read()
        {
            await base.Grain_AzureStore_Read();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKey_AzureTableGrainStorage_Read_Write()
        {
            await base.Grain_GuidKey_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKey_AzureTableGrainStorage_Read_Write()
        {
            await base.Grain_LongKey_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_LongKeyExtended_AzureTableGrainStorage_Read_Write()
        {
            await base.Grain_LongKeyExtended_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_GuidKeyExtended_AzureTableGrainStorage_Read_Write()
        {
            await base.Grain_GuidKeyExtended_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_Generic_AzureTableGrainStorage_Read_Write()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();

            await base.Grain_Generic_AzureStore_Read_Write();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_Generic_AzureTableGrainStorage_DiffTypes()
        {
            StorageEmulatorUtilities.EnsureEmulatorIsNotUsed();

            await base.Grain_Generic_AzureStore_DiffTypes();
        }

        [SkippableFact, TestCategory("Functional")]
        public async Task Grain_AzureTableGrainStorage_SiloRestart()
        {
            await base.Grain_AzureStore_SiloRestart();
        }
    }
}

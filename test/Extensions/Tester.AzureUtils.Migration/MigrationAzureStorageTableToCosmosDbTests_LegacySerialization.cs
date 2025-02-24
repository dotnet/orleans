#if NET8_0_OR_GREATER
using Orleans.ApplicationParts;
using Orleans.Hosting;
using Orleans.Persistence.Cosmos.Migration;
using Orleans.Persistence.Migration;
using Orleans.TestingHost;
using Tester.AzureUtils.Migration.Abstractions;
using Tester.AzureUtils.Migration.Grains;
using TestExtensions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Orleans.Serialization;
using Orleans.Runtime;
using Orleans.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Orleans;
using Azure.Identity;
using Tester.AzureUtils.Migration.Helpers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Persistence.Cosmos.DocumentIdProviders;
using Orleans.Persistence.Cosmos;

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureStorageTableToCosmosDbLegacySerializationTests : MigrationTableStorageToCosmosLegacySerializationTests, IClassFixture<MigrationAzureStorageTableToCosmosDbLegacySerializationTests.Fixture>
    {
        public static string OrleansDatabase = Resources.MigrationDatabase;
        public static string OrleansContainer = Resources.MigrationLegacyContainer; // container has different partition key '/pk'

        public static string RandomIdentifier = Guid.NewGuid().ToString().Replace("-", "");

        public MigrationAzureStorageTableToCosmosDbLegacySerializationTests(Fixture fixture) : base(fixture)
        {
        }

        public class Fixture : BaseAzureTestClusterFixture
        {
            protected override void ConfigureTestCluster(TestClusterBuilder builder)
            {
                builder.AddSiloBuilderConfigurator<SiloConfigurator>();
            }
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder siloBuilder)
            {
                siloBuilder
                    .ConfigureServices(services =>
                    {
                        services.TryAddSingleton<IDocumentIdProvider, ClusterDocumentIdProvider>();
                    });

                siloBuilder
                    .AddMigrationTools()
                    .AddMigrationGrainStorageAsDefault(options =>
                    {
                        options.SourceStorageName = SourceStorageName;
                        options.DestinationStorageName = DestinationStorageName;

                        options.WriteToDestinationOnly = false; // original storage will get the updated states as well
                    })
                    .AddAzureTableGrainStorage(SourceStorageName, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = $"source{RandomIdentifier}";
                    })
                    .AddMigrationAzureCosmosGrainStorage(DestinationStorageName, options =>
                    {
                        options.ConfigureCosmosStorageOptions();

                        // options.ContainerName = $"destination{RandomIdentifier}";
                        options.ContainerName = OrleansContainer;
                        options.DatabaseName = OrleansDatabase;

                        // which writes in non-orleans-8 compatible format
#pragma warning disable OrleansCosmosExperimental
                        options.UseExperimentalFormat = true;
#pragma warning restore OrleansCosmosExperimental
                    })
                    .AddDataMigrator(SourceStorageName, DestinationStorageName);
            }
        }
    }
}
#endif
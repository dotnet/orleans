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

namespace Tester.AzureUtils.Migration
{
    [TestCategory("Functionals"), TestCategory("Migration"), TestCategory("Azure"), TestCategory("AzureTableStorage")]
    public class MigrationAzureStorageTableToCosmosDbTests : MigrationTableStorageToCosmosTests, IClassFixture<MigrationAzureStorageTableToCosmosDbTests.Fixture>
    {
        public const string OrleansDatabase = "Orleans";
        public const string OrleansContainer = "destinationtest";

        public static string RandomIdentifier = Guid.NewGuid().ToString().Replace("-", "");

        public MigrationAzureStorageTableToCosmosDbTests(Fixture fixture) : base(fixture)
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
                    .AddMigrationTools()
                    .AddMigrationGrainStorageAsDefault(options =>
                    {
                        options.SourceStorageName = SourceStorageName;
                        options.DestinationStorageName = DestinationStorageName;
                    })
                    .AddAzureTableGrainStorage(SourceStorageName, options =>
                    {
                        options.ConfigureTestDefaults();
                        options.TableName = $"source{RandomIdentifier}";
                    })
                    .AddCosmosGrainStorage(DestinationStorageName, options =>
                    {
                        // options.ContainerName = $"destination{RandomIdentifier}";
                        options.ContainerName = $"destinationtest";
                        options.DatabaseName = "Orleans";
                        options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosConnectionString);
                    })
                    .AddDataMigrator(SourceStorageName, DestinationStorageName);
            }
        }
    }
}
#endif
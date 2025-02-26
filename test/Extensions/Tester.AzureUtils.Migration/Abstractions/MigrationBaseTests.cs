using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

#if NET7_0_OR_GREATER
using Orleans.Persistence.Migration;

#endif

#if NET8_0_OR_GREATER
using Orleans.Persistence.Cosmos;
using Azure.Data.Tables;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using System.Globalization;
using Tester.AzureUtils.Migration.Grains;
#endif

namespace Tester.AzureUtils.Migration.Abstractions
{
    public abstract class MigrationBaseTests
    {
        protected BaseAzureTestClusterFixture fixture;
        public const string SourceStorageName = "source-storage";
        public const string DestinationStorageName = "destination-storage";

        protected MigrationBaseTests(BaseAzureTestClusterFixture fixture)
        {
            this.fixture = fixture;
        }

        private IServiceProvider? serviceProvider;
        protected IServiceProvider ServiceProvider
        {
            get
            {
                if (this.serviceProvider == null)
                {
                    var silo = (InProcessSiloHandle)this.fixture.HostedCluster.Primary;
                    this.serviceProvider = silo.SiloHost.Services;
                }
                return this.serviceProvider;
            }
        }

        private ClusterOptions? clusterOptions;
        protected ClusterOptions ClusterOptions
        {
            get
            {
                if (this.clusterOptions == null)
                {
                    this.clusterOptions = ServiceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value;
                }
                return this.clusterOptions;
            }
        }

        private IGrainStorage? sourceStorage;
        protected IGrainStorage SourceStorage
        {
            get
            {
                if (this.sourceStorage == null)
                {
                    this.sourceStorage = ServiceProvider.GetRequiredServiceByName<IGrainStorage>(SourceStorageName);
                }
                return this.sourceStorage;
            }
        }

        protected IExtendedGrainStorage? SourceExtendedStorage
            => (SourceStorage as IExtendedGrainStorage) ?? null;

        private IGrainStorage? destinationStorage;
        protected IGrainStorage DestinationStorage
        {
            get
            {
                if (this.destinationStorage == null)
                {
                    this.destinationStorage = ServiceProvider.GetRequiredServiceByName<IGrainStorage>(DestinationStorageName);
                }
                return this.destinationStorage;
            }
        }

        private IGrainStorage? migrationStorage;
        protected IGrainStorage MigrationStorage
        {
            get
            {
                if (this.migrationStorage == null)
                {
                    this.migrationStorage = ServiceProvider.GetRequiredServiceByName<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
                }
                return this.migrationStorage;
            }
        }

#if NET7_0_OR_GREATER
        private IReminderMigrationTable? reminderTable;
        protected async Task<IReminderMigrationTable> GetAndInitReminderTableAsync()
        {
            if (reminderTable == null)
            {
                reminderTable = ServiceProvider.GetRequiredService<IReminderMigrationTable>();
                await reminderTable.Init();
            }

            return reminderTable;
        }

        private DataMigrator? dataMigrator;
        protected DataMigrator DataMigrator
        {
            get
            {
                if (this.dataMigrator == null)
                {
                    this.dataMigrator = ServiceProvider.GetRequiredService<DataMigrator>();
                }
                return this.dataMigrator;
            }
        }
#endif

#if NET8_0_OR_GREATER
        private IDocumentIdProvider? cosmosDocumentIdProvider;
        protected IDocumentIdProvider DocumentIdProvider
        {
            get
            {
                if (this.cosmosDocumentIdProvider == null)
                {
                    this.cosmosDocumentIdProvider = ServiceProvider.GetServiceByName<IDocumentIdProvider>(DestinationStorageName) ?? ServiceProvider.GetRequiredService<IDocumentIdProvider>();
                }
                return this.cosmosDocumentIdProvider;
            }
        }

        /// <summary>
        /// Loads currently stored grain state from Cosmos DB.
        /// </summary>
        /// <remarks>
        /// We can't call `DestinationStorage.ReadAsync()` because of the inner implementation details
        /// </remarks>
        protected async Task<JToken> GetGrainStateJsonFromCosmosAsync(
            CosmosClient cosmosClient,
            string databaseName,
            string containerName,
            IDocumentIdProvider documentIdProvider,
            GrainReference grain,
            string? stateName = "state", // when cosmos is a target storage for migration, state is the default name of how Orleans writes a partitionKey
            bool latestOrleansSerializationFormat = true
        )
        {
            var database = cosmosClient.GetDatabase(databaseName);
            var container = database.Client.GetContainer(database.Id, containerName);

            var grainId = grain.GetPrimaryKeyLong();
            var grainIdRepresentation = grainId.ToString("X", CultureInfo.InvariantCulture); // document number is represented in Cosmos in such a way
            var (documentId, partitionKey) = documentIdProvider.GetDocumentIdentifiers(
                stateName!,
                "migrationtestgrain", // GrainTypeAttribute's value for MigrationTestGrain
                grainIdRepresentation);

            var response = await container.ReadItemAsync<dynamic>(documentId, new PartitionKey(partitionKey));
            JObject data = response.Resource;

            var dataState = latestOrleansSerializationFormat
                ? data["State"]!
                : data["state"]!;

            if (dataState is null)
            {
                throw new InvalidDataException("Grain state is null");
            }

            return dataState;
        }

        /// <summary>
        /// Loads currently stored grain state from Cosmos DB.
        /// </summary>
        /// <remarks>
        /// We can't call `DestinationStorage.ReadAsync()` because of the inner implementation details
        /// </remarks>
        protected async Task<MigrationTestGrain_State> GetGrainStateFromCosmosAsync(
            CosmosClient cosmosClient,
            string databaseName,
            string containerName,
            IDocumentIdProvider documentIdProvider,
            GrainReference grain,
            string? stateName = "state", // when cosmos is a target storage for migration, state is the default name of how Orleans writes a partitionKey
            bool latestOrleansSerializationFormat = true
        )
        {
            var dataState = await GetGrainStateJsonFromCosmosAsync(cosmosClient, databaseName, containerName, documentIdProvider, grain, stateName, latestOrleansSerializationFormat);

            return new MigrationTestGrain_State
            {
                A = dataState["A"]!.Value<int>(),
                B = dataState["B"]!.Value<int>()
            };
        }
#endif
    }
}
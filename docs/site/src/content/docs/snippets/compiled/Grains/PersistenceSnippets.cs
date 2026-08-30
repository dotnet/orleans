using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Hosting;
using Orleans.Storage;

namespace Documentation.Grains.Persistence.Cosmos
{
    // <azure_identity_using_cosmos>
using Azure.Identity;
    // </azure_identity_using_cosmos>
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Persistence.Cosmos;

    internal static class CosmosStorage
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <configure_cosmos_storage>
siloBuilder.AddCosmosGrainStorage(
    "profileStore",
    options =>
    {
        options.ConfigureCosmosClient(
            "https://account.documents.azure.com:443/",
            new DefaultAzureCredential());
        options.DatabaseName = "Orleans";
        options.ContainerName = "OrleansStorage";
        options.IsResourceCreationEnabled = false;
    });
            // </configure_cosmos_storage>
        }
    }

    // <cosmos_hpk_document_id_provider>
    /// <summary>
    /// Derives a document ID and two ordered partition-key values for each grain record.
    /// </summary>
    public sealed class TenantDocumentIdProvider(IOptions<ClusterOptions> clusterOptions) : IDocumentIdProvider
    {
        private readonly DefaultDocumentIdProvider _defaultProvider = new(clusterOptions);

        /// <inheritdoc />
        public ValueTask<(string DocumentId, string PartitionKey)> GetDocumentIdentifiers(
            string grainType,
            GrainId grainId)
        {
            var tenantId = grainId.Key.ToString()!;
            return new((_defaultProvider.GetId(grainType, grainId), tenantId));
        }

        /// <inheritdoc />
        public ValueTask<CosmosDocumentKey> GetDocumentKey(string grainType, GrainId grainId)
        {
            var tenantId = grainId.Key.ToString()!;
            return new(new CosmosDocumentKey(
                _defaultProvider.GetId(grainType, grainId),
                [tenantId, grainType]));
        }
    }
    // </cosmos_hpk_document_id_provider>

    internal static class CosmosHierarchicalPartitionKeys
    {
        internal static void Configure(ISiloBuilder siloBuilder, string connectionString)
        {
            // <configure_cosmos_hpk_storage>
siloBuilder.AddCosmosGrainStorage<TenantDocumentIdProvider>(
    "cosmosStore",
    options =>
    {
        options.ConfigureCosmosClient(connectionString);
        options.PartitionKeyLevelCount = 2;
    });
            // </configure_cosmos_hpk_storage>
        }
    }
}

namespace Documentation.Grains.Persistence.AzureTable
{
    // <azure_identity_using_table>
using Azure.Data.Tables;
using Azure.Identity;
    // </azure_identity_using_table>

    internal static class AzureTableStorage
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <configure_azure_table_storage>
siloBuilder.AddAzureTableGrainStorage(
    "profileStore",
    options => options.TableServiceClient = new TableServiceClient(
        new Uri("https://account.table.core.windows.net"),
        new DefaultAzureCredential()));
            // </configure_azure_table_storage>
        }
    }
}

namespace Documentation.Grains.Persistence.AzureBlob
{
    // <azure_identity_using_blob>
using Azure.Identity;
using Azure.Storage.Blobs;
    // </azure_identity_using_blob>

    internal static class AzureBlobStorage
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <configure_azure_blob_storage>
siloBuilder.AddAzureBlobGrainStorage(
    "cartStore",
    options => options.BlobServiceClient = new BlobServiceClient(
        new Uri("https://account.blob.core.windows.net"),
        new DefaultAzureCredential()));
            // </configure_azure_blob_storage>
        }
    }
}

namespace Documentation.Grains.Persistence.DynamoDb
{
    internal static class DynamoDbStorage
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <configure_dynamodb_storage>
siloBuilder.AddDynamoDBGrainStorage(
    "profileStore",
    options =>
    {
        options.Service = "us-west-2";
        options.ServiceId = "my-application";
        options.TableName = "OrleansGrainState";
        options.CreateIfNotExists = false;
    });
            // </configure_dynamodb_storage>
        }
    }
}

namespace Documentation.Grains.Persistence.Relational
{
    internal static class RelationalStorage
    {
        internal static void Configure(string[] args)
        {
            // <configure_ado_net_storage>
var builder = Host.CreateApplicationBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    siloBuilder.AddAdoNetGrainStorage(
        "stateStore",
        options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString =
                builder.Configuration.GetConnectionString("grainState")
                ?? throw new InvalidOperationException(
                    "The grainState connection string isn't configured.");
        });
});
            // </configure_ado_net_storage>
        }

        internal static void ConfigureSqlite(ISiloBuilder siloBuilder)
        {
            // <configure_sqlite_storage>
siloBuilder.AddAdoNetGrainStorage(
    "localState",
    options =>
    {
        options.Invariant = "System.Data.SQLite";
        options.ConnectionString = "Data Source=orleans-state.db";
    });
            // </configure_sqlite_storage>
        }
    }
}

namespace Documentation.Grains.Persistence.Custom
{
    internal sealed class MyGrainStorage : IGrainStorage
    {
        public MyGrainStorage(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
        }

        public Task ClearStateAsync<T>(
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState) =>
            Task.CompletedTask;

        public Task ReadStateAsync<T>(
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState) =>
            Task.CompletedTask;

        public Task WriteStateAsync<T>(
            string stateName,
            GrainId grainId,
            IGrainState<T> grainState) =>
            Task.CompletedTask;
    }

    internal static class CustomStorage
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <register_custom_grain_storage>
siloBuilder.Services.AddGrainStorage<MyGrainStorage>(
    "custom",
    (services, name) => new MyGrainStorage(name));
            // </register_custom_grain_storage>
        }
    }
}

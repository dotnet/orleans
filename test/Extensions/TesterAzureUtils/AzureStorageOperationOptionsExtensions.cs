using Azure.Data.Tables;
using Azure.Storage.Blobs;
using TestExtensions;

namespace Tester.AzureUtils
{
    public static class AzureStorageOperationOptionsExtensions
    {
        public static Orleans.Clustering.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Clustering.AzureStorage.AzureStorageOperationOptions options)
        {
            options.TableServiceClient = GetTableServiceClient();

            return options;
        }

        public static TableServiceClient GetTableServiceClient()
        {
            return new(AzuriteContainerManager.ConnectionString);
        }

        public static Orleans.GrainDirectory.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.GrainDirectory.AzureStorage.AzureStorageOperationOptions options)
        {
            options.TableServiceClient = GetTableServiceClient();

            return options;
        }

        public static Orleans.Persistence.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Persistence.AzureStorage.AzureStorageOperationOptions options)
        {
            options.TableServiceClient = GetTableServiceClient();

            return options;
        }

        public static Orleans.Reminders.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Reminders.AzureStorage.AzureStorageOperationOptions options)
        {
            options.TableServiceClient = GetTableServiceClient();

            return options;
        }

        public static Orleans.Configuration.AzureBlobStorageOptions ConfigureTestDefaults(this Orleans.Configuration.AzureBlobStorageOptions options)
        {
            options.BlobServiceClient = new(AzuriteContainerManager.ConnectionString);

            return options;
        }

        public static AzureStorageJobShardOptions ConfigureTestDefaults(this AzureStorageJobShardOptions options)
        {
            options.BlobServiceClient = new(AzuriteContainerManager.ConnectionString);

            return options;
        }

        public static Orleans.Configuration.AzureQueueOptions ConfigureTestDefaults(this Orleans.Configuration.AzureQueueOptions options)
        {
            options.QueueServiceClient = new(AzuriteContainerManager.ConnectionString);

            return options;
        }

        public static Orleans.Configuration.AzureBlobLeaseProviderOptions ConfigureTestDefaults(this Orleans.Configuration.AzureBlobLeaseProviderOptions options)
        {
            options.BlobServiceClient = new(AzuriteContainerManager.ConnectionString);

            return options;
        }
    }
}

using TestExtensions;

namespace TesterInternal.AzureInfra
{
    public static class AzureStorageOperationOptionsExtensions
    {
        public static Orleans.Configuration.AzureTableStorageOptions ConfigureTestDefaults(this Orleans.Configuration.AzureTableStorageOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }

        public static Orleans.Clustering.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Clustering.AzureStorage.AzureStorageOperationOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }

        public static Orleans.GrainDirectory.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.GrainDirectory.AzureStorage.AzureStorageOperationOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }

        public static Orleans.Persistence.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Persistence.AzureStorage.AzureStorageOperationOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }

        public static Orleans.Reminders.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Reminders.AzureStorage.AzureStorageOperationOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureTableServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }

        public static Orleans.Configuration.AzureBlobStorageOptions ConfigureTestDefaults(this Orleans.Configuration.AzureBlobStorageOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataBlobUri, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }

        public static Orleans.Configuration.AzureQueueOptions ConfigureTestDefaults(this Orleans.Configuration.AzureQueueOptions options)
        {
#if DEBUG
            options.ConfigureQueueServiceClient(TestDefaultConfiguration.DataQueueUri, TestDefaultConfiguration.TokenCredential);
            return options;
#else
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureQueueServiceClient(TestDefaultConfiguration.DataQueueUri, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureQueueServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
#endif
        }

        public static Orleans.Configuration.AzureBlobLeaseProviderOptions ConfigureTestDefaults(this Orleans.Configuration.AzureBlobLeaseProviderOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataBlobUri, TestDefaultConfiguration.TokenCredential);
            }
            else
            {
                options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataConnectionString);
            }

            return options;
        }
    }
}

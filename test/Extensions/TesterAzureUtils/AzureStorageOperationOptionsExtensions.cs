using Azure.Identity;
using TestExtensions;

namespace Tester.AzureUtils
{
    public static class AzureStorageOperationOptionsExtensions
    {
        public static Orleans.Clustering.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Clustering.AzureStorage.AzureStorageOperationOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.TableEndpoint = TestDefaultConfiguration.TableEndpoint;
                options.TableResourceId = TestDefaultConfiguration.TableResourceId;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
            }

            return options;
        }

        public static Orleans.GrainDirectory.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.GrainDirectory.AzureStorage.AzureStorageOperationOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.TableEndpoint = TestDefaultConfiguration.TableEndpoint;
                options.TableResourceId = TestDefaultConfiguration.TableResourceId;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
            }

            return options;
        }

        public static Orleans.Persistence.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Persistence.AzureStorage.AzureStorageOperationOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.TableEndpoint = TestDefaultConfiguration.TableEndpoint;
                options.TableResourceId = TestDefaultConfiguration.TableResourceId;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
            }

            return options;
        }

        public static Orleans.Reminders.AzureStorage.AzureStorageOperationOptions ConfigureTestDefaults(this Orleans.Reminders.AzureStorage.AzureStorageOperationOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.TableEndpoint = TestDefaultConfiguration.TableEndpoint;
                options.TableResourceId = TestDefaultConfiguration.TableResourceId;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
            }

            return options;
        }

        public static Orleans.Configuration.AzureBlobStorageOptions ConfigureTestDefaults(this Orleans.Configuration.AzureBlobStorageOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ServiceUri = TestDefaultConfiguration.DataBlobUri;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
            }

            return options;
        }

        public static Orleans.Configuration.AzureQueueOptions ConfigureTestDefaults(this Orleans.Configuration.AzureQueueOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ServiceUri = TestDefaultConfiguration.DataQueueUri;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.ConnectionString = TestDefaultConfiguration.DataConnectionString;
            }

            return options;
        }

        public static Orleans.Configuration.AzureBlobLeaseProviderOptions ConfigureTestDefaults(this Orleans.Configuration.AzureBlobLeaseProviderOptions options)
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ServiceUri = TestDefaultConfiguration.DataBlobUri;
                options.TokenCredential = new DefaultAzureCredential();
            }
            else
            {
                options.DataConnectionString = TestDefaultConfiguration.DataConnectionString;
            }

            return options;
        }
    }
}

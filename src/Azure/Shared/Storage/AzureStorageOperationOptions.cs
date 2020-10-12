using System;
using Azure.Core;
using Microsoft.Azure.Cosmos.Table;
using Orleans.Runtime;

#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.AzureStorage
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureStorage
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.AzureStorage
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureStorage
#elif ORLEANS_EVENTHUBS
namespace Orleans.Streaming.EventHubs
#elif TESTER_AZUREUTILS
namespace Orleans.Tests.AzureUtils
#elif ORLEANS_TRANSACTIONS
namespace Orleans.Transactions.AzureStorage
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.AzureStorage
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    public class AzureStorageOperationOptions
    {
        /// <summary>
        /// Azure Storage Policy Options
        /// </summary>
        public AzureStoragePolicyOptions StoragePolicyOptions { get; } = new AzureStoragePolicyOptions();
        
        /// <summary>
        /// Connection string for Azure Cosmos DB Table
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Use AAD to retrieve the account key
        /// </summary>
        public TokenCredential TokenCredential { get; set; }

        /// <summary>
        /// The table endpoint (e.g. https://x.table.cosmos.azure.com.) Required for specifying <see cref="TokenCredential"/>.
        /// </summary>
        public Uri TableEndpoint { get; set; }

        /// <summary>
        /// If <see cref="TokenCredential"/> is used, determines, sets the ID of the table storage account
        /// (e.g. <c>/subscriptions/88e5ceb6-26bd-4bf5-8933-f4e05fd9efa6/resourceGroups/rg1/providers/Microsoft.DocumentDB/databaseAccounts/ac1</c>)
        /// </summary>
        public string TableResourceId { get; set; }

        /// <summary>
        /// If <see cref="TokenCredential"/> is used, determines the type of key.
        /// </summary>
        public TokenCredentialTableKey TokenCredentialTableKey { get; set; }

        /// <summary>
        /// If <see cref="TokenCredential"/> is used, determines the management endpoint.
        /// </summary>
        public Uri TokenCredentialManagementUri { get; set; } = new Uri("https://management.azure.com/");

        /// <summary>
        /// Table name for Azure Storage
        /// </summary>
        public virtual string TableName { get; set; }
    }

    public class AzureStorageOperationOptionsValidator<TOptions> : IConfigurationValidator where TOptions : AzureStorageOperationOptions
    {
        public AzureStorageOperationOptionsValidator(TOptions options, string name = null)
        {
            Options = options;
            Name = name;
        }

        public TOptions Options { get; }
        public string Name { get; }

        public virtual void ValidateConfiguration()
        {
            if (Options.TokenCredential != null)
            {
                if (Options.TableEndpoint == null)
                    throw GetException($"{nameof(Options.TableEndpoint)} is required.");

                if (string.IsNullOrEmpty(Options.TableResourceId))
                    throw GetException($"{nameof(Options.TableResourceId)} is required.");

                if (Options.TokenCredentialManagementUri == null)
                    throw GetException($"{nameof(Options.TokenCredentialManagementUri)} is required.");
            }
            else
            {
                if (!CloudStorageAccount.TryParse(Options.ConnectionString, out _))
                    throw GetException($"{nameof(Options.ConnectionString)} is not valid.");
            }

            try
            {
                AzureTableUtils.ValidateTableName(this.Options.TableName);
            }
            catch (Exception ex)
            {
                throw GetException($"{nameof(Options.TableName)} is not valid.", ex);
            }

            Exception GetException(string message, Exception inner = null) =>
                new OrleansConfigurationException($"Configuration for {GetType().Name} {Name} is invalid. {message}", inner);
        }
    }

    public enum TokenCredentialTableKey
    {
        Primary,
        Secondary
    }
}

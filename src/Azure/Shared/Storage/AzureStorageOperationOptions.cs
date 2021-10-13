using System;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
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
        /// Table name for Azure Storage
        /// </summary>
        public virtual string TableName { get; set; }

        /// <summary>
        /// Azure Storage Policy Options
        /// </summary>
        public AzureStoragePolicyOptions StoragePolicyOptions { get; } = new AzureStoragePolicyOptions();

        /// <summary>
        /// Options to be used when configuring the table storage client, or <see langword="null"/> to use the default options.
        /// </summary>
        public TableClientOptions ClientOptions { get; set; }

        /// <summary>
        /// The delegate used to create a <see cref="TableServiceClient"/> instance.
        /// </summary>
        internal Func<Task<TableServiceClient>> CreateClient { get; private set; }

        /// <summary>
        /// Configures the <see cref="TableServiceClient"/> using a connection string.
        /// </summary>
        public void ConfigureTableServiceClient(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentNullException(nameof(connectionString));
            CreateClient = () => Task.FromResult(new TableServiceClient(connectionString, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="TableServiceClient"/> using an authenticated service URI.
        /// </summary>
        public void ConfigureTableServiceClient(Uri serviceUri)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            CreateClient = () => Task.FromResult(new TableServiceClient(serviceUri, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="TableServiceClient"/> using the provided callback.
        /// </summary>
        public void ConfigureTableServiceClient(Func<Task<TableServiceClient>> createClientCallback)
        {
            CreateClient = createClientCallback ?? throw new ArgumentNullException(nameof(createClientCallback));
        }

        /// <summary>
        /// Configures the <see cref="TableServiceClient"/> using an authenticated service URI and a <see cref="Azure.Core.TokenCredential"/>.
        /// </summary>
        public void ConfigureTableServiceClient(Uri serviceUri, TokenCredential tokenCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (tokenCredential is null) throw new ArgumentNullException(nameof(tokenCredential));
            CreateClient = () => Task.FromResult(new TableServiceClient(serviceUri, tokenCredential, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="TableServiceClient"/> using an authenticated service URI and a <see cref="Azure.AzureSasCredential"/>.
        /// </summary>
        public void ConfigureTableServiceClient(Uri serviceUri, AzureSasCredential azureSasCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (azureSasCredential is null) throw new ArgumentNullException(nameof(azureSasCredential));
            CreateClient = () => Task.FromResult(new TableServiceClient(serviceUri, azureSasCredential, ClientOptions));
        }

        /// <summary>
        /// Configures the <see cref="TableServiceClient"/> using an authenticated service URI and a <see cref="TableSharedKeyCredential"/>.
        /// </summary>
        public void ConfigureTableServiceClient(Uri serviceUri, TableSharedKeyCredential sharedKeyCredential)
        {
            if (serviceUri is null) throw new ArgumentNullException(nameof(serviceUri));
            if (sharedKeyCredential is null) throw new ArgumentNullException(nameof(sharedKeyCredential));
            CreateClient = () => Task.FromResult(new TableServiceClient(serviceUri, sharedKeyCredential, ClientOptions));
        }

        internal void Validate(string name)
        {
            if (CreateClient is null)
            {
                throw new OrleansConfigurationException($"No credentials specified. Use the {GetType().Name}.{nameof(AzureStorageOperationOptions.ConfigureTableServiceClient)} method to configure the Azure Table Service client.");
            }

            try
            {
                AzureTableUtils.ValidateTableName(TableName);
            }
            catch (Exception ex)
            {
                throw GetException($"{nameof(TableName)} is not valid.", ex);
            }

            Exception GetException(string message, Exception inner = null) =>
                new OrleansConfigurationException($"Configuration for {GetType().Name} {name} is invalid. {message}", inner);
        }
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
            Options.Validate(Name);
        }
    }
}

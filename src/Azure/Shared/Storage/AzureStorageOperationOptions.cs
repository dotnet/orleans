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
        /// Connection string.
        /// </summary>
        /// <remarks>
        /// This property is superseded by all other properties except for <see cref="ServiceUri"/>.
        /// </remarks>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Token credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/>, <see cref="AzureSasCredential"/>, and <see cref="SharedKeyCredential"/>.
        /// This property is superseded by <see cref="CreateClient"/>.
        /// </remarks>
        public TokenCredential TokenCredential { get; set; }

        /// <summary>
        /// Azure SAS credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/> and <see cref="SharedKeyCredential"/>.
        /// This property is superseded by <see cref="CreateClient"/> and <see cref="TokenCredential"/>.
        /// </remarks>
        public AzureSasCredential AzureSasCredential { get; set; }

        /// <summary>
        /// Options to be used when configuring the table storage client, or <see langword="null"/> to use the default options.
        /// </summary>
        public TableClientOptions ClientOptions { get; set; }

        /// <summary>
        /// The table service endpoint (e.g. https://x.table.cosmos.azure.com.), which can included a shared access signature.
        /// </summary>
        /// <remarks>
        /// If this property contains a shared access signature, then no other credential properties are required.
        /// Otherwise, the presence of any other credential property will take precedence over this.
        /// </remarks>
        public Uri ServiceUri { get; set; }

        /// <summary>
        /// Shared key credentials, to be used in conjunction with <see cref="ServiceUri"/>.
        /// </summary>
        /// <remarks>
        /// This property takes precedence over specifying only <see cref="ServiceUri"/> and over <see cref="ConnectionString"/>.
        /// This property is superseded by <see cref="CreateClient"/>, <see cref="TokenCredential"/>, and <see cref="AzureSasCredential"/>.
        /// </remarks>
        public TableSharedKeyCredential SharedKeyCredential { get; set; }

        /// <summary>
        /// The optional delegate used to create a <see cref="TableServiceClient"/> instance.
        /// </summary>
        /// <remarks>
        /// This property, if not <see langword="null"/>, takes precedence over <see cref="ConnectionString"/>, <see cref="SharedKeyCredential"/>, <see cref="AzureSasCredential"/>, <see cref="TokenCredential"/>, <see cref="ClientOptions"/>, and <see cref="ServiceUri"/>,
        /// </remarks>
        public Func<Task<TableServiceClient>> CreateClient { get; set; }

        /// <summary>
        /// Sets credential properties using an authenticated service URI.
        /// </summary>
        public void SetCredentials(Uri serviceUri)
        {
            ClearCredentials();
            ServiceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
        }

        /// <summary>
        /// Sets credential properties using a connection string.
        /// </summary>
        public void SetCredentials(string connectionString)
        {
            ClearCredentials();
            ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <summary>
        /// Sets credential properties using an authenticated service URI and a <see cref="Azure.Core.TokenCredential"/>.
        /// </summary>
        public void SetCredentials(Uri serviceUri, TokenCredential tokenCredential)
        {
            ClearCredentials();
            ServiceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
            TokenCredential = tokenCredential ?? throw new ArgumentNullException(nameof(tokenCredential));
        }

        /// <summary>
        /// Sets credential properties using an authenticated service URI and a <see cref="Azure.AzureSasCredential"/>.
        /// </summary>
        public void SetCredentials(Uri serviceUri, AzureSasCredential azureSasCredential)
        {
            ClearCredentials();
            ServiceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
            AzureSasCredential = azureSasCredential ?? throw new ArgumentNullException(nameof(azureSasCredential));
        }

        /// <summary>
        /// Sets credential properties using an authenticated service URI and a <see cref="TableSharedKeyCredential"/>.
        /// </summary>
        public void SetCredentials(Uri serviceUri, TableSharedKeyCredential sharedKeyCredential)
        {
            ClearCredentials();
            ServiceUri = serviceUri ?? throw new ArgumentNullException(nameof(serviceUri));
            SharedKeyCredential = sharedKeyCredential ?? throw new ArgumentNullException(nameof(sharedKeyCredential));
        }

        private void ClearCredentials()
        {
            ServiceUri = default;
            TokenCredential = default;
            ConnectionString = default;
            AzureSasCredential = default;
            SharedKeyCredential = default;
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
            if (Options.CreateClient is { } createTableClient)
            {
                ThrowIfNotNull(Options.TokenCredential, nameof(Options.TokenCredential), nameof(Options.TokenCredential));
                ThrowIfNotNull(Options.AzureSasCredential, nameof(Options.AzureSasCredential), nameof(Options.AzureSasCredential));
                ThrowIfNotNull(Options.SharedKeyCredential, nameof(Options.SharedKeyCredential), nameof(Options.SharedKeyCredential));
                ThrowIfNotNull(Options.ConnectionString, nameof(Options.ConnectionString), nameof(Options.ConnectionString));
                ThrowIfNotNull(Options.ServiceUri, nameof(Options.ServiceUri), nameof(Options.ServiceUri));
            }
            else if (Options.TokenCredential is { } tokenCredential)
            {
                ValidateUrl(Options, nameof(Options.TokenCredential));
                ThrowIfNotNull(Options.AzureSasCredential, nameof(Options.AzureSasCredential), nameof(Options.AzureSasCredential));
                ThrowIfNotNull(Options.SharedKeyCredential, nameof(Options.SharedKeyCredential), nameof(Options.SharedKeyCredential));
                ThrowIfNotNull(Options.ConnectionString, nameof(Options.ConnectionString), nameof(Options.ConnectionString));
            }
            else if (Options.AzureSasCredential is { } sasCredential)
            {
                ValidateUrl(Options, nameof(Options.AzureSasCredential));
                ThrowIfNotNull(Options.SharedKeyCredential, nameof(Options.SharedKeyCredential), nameof(Options.SharedKeyCredential));
                ThrowIfNotNull(Options.ConnectionString, nameof(Options.ConnectionString), nameof(Options.ConnectionString));
            }
            else if (Options.SharedKeyCredential is { } tableSharedKeyCredential)
            {
                ValidateUrl(Options, nameof(Options.SharedKeyCredential));
                ThrowIfNotNull(Options.ConnectionString, nameof(Options.ConnectionString), nameof(Options.ConnectionString));
            }
            else if (Options.ConnectionString is { Length: > 0 } connectionString)
            {
                ThrowIfNotNull(Options.ServiceUri, nameof(Options.ServiceUri), nameof(Options.ConnectionString));
            }
            else
            {
                throw new InvalidOperationException($"{nameof(Options.ServiceUri)} is null, but it is required when no other credential is specified");
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

            static void ValidateUrl(AzureStorageOperationOptions options, string dependentOption)
            {
                if (options.ServiceUri is null)
                {
                    throw new InvalidOperationException($"{nameof(options.ServiceUri)} is null, but it is required when {dependentOption} is specified");
                }
            }

            static void ThrowIfNotNull(object value, string propertyName, string dependentOption)
            {
                if (value is not null)
                {
                    throw new InvalidOperationException($"{propertyName} is not null, but it is not being used because {dependentOption} has been set and takes precedence");
                }
            }
        }
    }
}

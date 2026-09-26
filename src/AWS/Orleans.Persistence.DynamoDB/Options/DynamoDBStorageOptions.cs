using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Persistence.DynamoDB;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the DynamoDB grain storage provider.
    /// </summary>
    public class DynamoDBStorageOptions : DynamoDBClientOptions, IStorageProviderSerializerOptions
    {
        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment.
        /// When left empty, see <see cref="UseClusterServiceId"/>.
        /// </summary>
        public string ServiceId { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether an empty <see cref="ServiceId"/> falls back to <see cref="ClusterOptions.ServiceId"/>,
        /// as the other grain storage providers do. When <see langword="false"/>, an empty <see cref="ServiceId"/> is used
        /// as it is, and every key starts with an underscore. When not set, the key format already recorded in the table is
        /// kept, and a new table uses the empty <see cref="ServiceId"/>; this default changes in a future major version.
        /// Has no effect when <see cref="ServiceId"/> is set.
        /// </summary>
        public bool? UseClusterServiceId { get; set; }

        /// <summary>
        /// Gets or sets whether state written with an empty <see cref="ServiceId"/> is read when a grain has no state under
        /// <see cref="ClusterOptions.ServiceId"/>, and moved there on the grain's next write. When not set, a migration
        /// recorded in the table goes on. Has an effect only when <see cref="ClusterOptions.ServiceId"/> is used for an
        /// empty <see cref="ServiceId"/>. Every silo must run with the same options while it is set: a silo still writing
        /// the keys with an empty <see cref="ServiceId"/> is not protected from a migrating one.
        /// </summary>
        public bool? MigrateLegacyKeys { get; set; }

        /// <summary>
        /// The <see cref="ClusterOptions.ServiceId"/> of the silo, used when <see cref="UseClusterServiceId"/> applies.
        /// </summary>
        internal string ClusterServiceId { get; set; } = ClusterOptions.DefaultServiceId;

        /// <summary>
        /// Use Provisioned Throughput for tables
        /// </summary>
        public bool UseProvisionedThroughput { get; set; } = true;

        /// <summary>
        /// Create the table if it doesn't exist
        /// </summary>
        public bool CreateIfNotExists { get; set; } = true;

        /// <summary>
        /// Update the table if it exists
        /// </summary>
        public bool UpdateIfExists { get; set; } = true;

        /// <summary>
        /// Read capacity unit for DynamoDB storage
        /// </summary>
        public int ReadCapacityUnits { get; set; } = DynamoDBStorage.DefaultReadCapacityUnits;

        /// <summary>
        /// Write capacity unit for DynamoDB storage
        /// </summary>
        public int WriteCapacityUnits { get; set; } = DynamoDBStorage.DefaultWriteCapacityUnits;

        /// <summary>
        /// DynamoDB table name.
        /// Defaults to 'OrleansGrainState'.
        /// </summary>
        public string TableName { get; set; } = "OrleansGrainState";

        /// <summary>
        /// Indicates if grain data should be deleted or reset to defaults when a grain clears it's state.
        /// </summary>
        public bool DeleteStateOnClear { get; set; } = false;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;

        /// <summary>
        /// The default silo lifecycle stage at which the storage provider is initialized.
        /// </summary>
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        /// <summary>
        /// Specifies a time span in which the item would be expired in the future
        /// every StateWrite will increase the TTL of the grain
        /// </summary>
        public TimeSpan? TimeToLive { get; set; }

        /// <inheritdoc/>
        public IGrainStorageSerializer GrainStorageSerializer { get; set; } = null!;
    }

    /// <summary>
    /// Supplies <see cref="DynamoDBStorageOptions"/> with the <see cref="ClusterOptions.ServiceId"/> of the silo.
    /// </summary>
    internal sealed class DynamoDBStorageClusterServiceIdConfigurator(IServiceProvider serviceProvider) : IPostConfigureOptions<DynamoDBStorageOptions>
    {
        public void PostConfigure(string? name, DynamoDBStorageOptions options)
        {
            // a provider-specific override first, as GetProviderClusterOptions does, without requiring ClusterOptions
            var clusterOptions = (name is null ? null : serviceProvider.GetKeyedService<ClusterOptions>(name))
                ?? serviceProvider.GetService<IOptions<ClusterOptions>>()?.Value;

            if (clusterOptions?.ServiceId is { Length: > 0 } serviceId)
            {
                options.ClusterServiceId = serviceId;
            }
        }
    }

    /// <summary>
    /// Configuration validator for DynamoDBStorageOptions
    /// </summary>
    public class DynamoDBGrainStorageOptionsValidator : IConfigurationValidator
    {
        private readonly DynamoDBStorageOptions options;
        private readonly string name;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        public DynamoDBGrainStorageOptionsValidator(DynamoDBStorageOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }

        /// <inheritdoc/>
        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.TableName))
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.TableName)} is not valid.");

            if (this.options.UseProvisionedThroughput)
            {
                if (this.options.ReadCapacityUnits == 0)
                    throw new OrleansConfigurationException(
                        $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.ReadCapacityUnits)} is not valid.");

                if (this.options.WriteCapacityUnits == 0)
                    throw new OrleansConfigurationException(
                        $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.WriteCapacityUnits)} is not valid.");
            }
        }
    }
}

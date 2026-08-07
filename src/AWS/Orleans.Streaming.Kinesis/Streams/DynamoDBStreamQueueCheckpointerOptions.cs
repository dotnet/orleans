using System;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures DynamoDB-backed stream queue checkpointing.
    /// </summary>
    public sealed class DynamoDBStreamQueueCheckpointerOptions
    {
        /// <summary>
        /// Gets or sets the AWS region name or DynamoDB service endpoint.
        /// </summary>
        public string Service { get; set; } = "us-east-1";

        /// <summary>
        /// Gets or sets the AWS access key. When omitted, the default AWS credential chain is used.
        /// </summary>
        [Redact]
        public string? AccessKey { get; set; }

        /// <summary>
        /// Gets or sets the AWS secret key.
        /// </summary>
        [Redact]
        public string? SecretKey { get; set; }

        /// <summary>
        /// Gets or sets the AWS session token.
        /// </summary>
        [Redact]
        public string? Token { get; set; }

        /// <summary>
        /// Gets or sets the AWS profile name.
        /// </summary>
        public string? ProfileName { get; set; }

        /// <summary>
        /// Gets or sets the DynamoDB table name.
        /// </summary>
        public string TableName { get; set; } = "OrleansStreamCheckpoints";

        /// <summary>
        /// Gets or sets a value indicating whether to create the checkpoint table when it does not exist.
        /// </summary>
        public bool CreateIfNotExists { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether the table uses provisioned throughput instead of on-demand billing.
        /// </summary>
        public bool UseProvisionedThroughput { get; set; }

        /// <summary>
        /// Gets or sets the provisioned read capacity units.
        /// </summary>
        public int ReadCapacityUnits { get; set; } = 10;

        /// <summary>
        /// Gets or sets the provisioned write capacity units.
        /// </summary>
        public int WriteCapacityUnits { get; set; } = 5;

        /// <summary>
        /// Gets or sets the minimum interval between checkpoint writes.
        /// </summary>
        public TimeSpan PersistInterval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Gets or sets the maximum time to wait for the checkpoint table to become active.
        /// </summary>
        public TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromMinutes(2);
    }

    internal sealed class DynamoDBStreamQueueCheckpointerOptionsValidator(
        DynamoDBStreamQueueCheckpointerOptions options,
        string name) : IConfigurationValidator
    {
        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(options.Service))
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBStreamQueueCheckpointerOptions.Service)} is required for the DynamoDB checkpointer '{name}'.");
            }

            if (string.IsNullOrWhiteSpace(options.TableName))
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBStreamQueueCheckpointerOptions.TableName)} is required for the DynamoDB checkpointer '{name}'.");
            }

            if (options.PersistInterval <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBStreamQueueCheckpointerOptions.PersistInterval)} must be greater than zero for the DynamoDB checkpointer '{name}'.");
            }

            if (options.InitializationTimeout <= TimeSpan.Zero)
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBStreamQueueCheckpointerOptions.InitializationTimeout)} must be greater than zero for the DynamoDB checkpointer '{name}'.");
            }

            if (options.UseProvisionedThroughput
                && (options.ReadCapacityUnits <= 0 || options.WriteCapacityUnits <= 0))
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBStreamQueueCheckpointerOptions.ReadCapacityUnits)} and " +
                    $"{nameof(DynamoDBStreamQueueCheckpointerOptions.WriteCapacityUnits)} must be greater than zero " +
                    $"when provisioned throughput is enabled for the DynamoDB checkpointer '{name}'.");
            }

            var hasAccessKey = !string.IsNullOrEmpty(options.AccessKey);
            var hasSecretKey = !string.IsNullOrEmpty(options.SecretKey);
            if (hasAccessKey != hasSecretKey)
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBStreamQueueCheckpointerOptions.AccessKey)} and " +
                    $"{nameof(DynamoDBStreamQueueCheckpointerOptions.SecretKey)} must either both be configured or both be omitted " +
                    $"for the DynamoDB checkpointer '{name}'.");
            }

            if (!string.IsNullOrEmpty(options.Token) && !hasAccessKey)
            {
                throw new OrleansConfigurationException(
                    $"The {nameof(DynamoDBStreamQueueCheckpointerOptions.Token)} requires explicit credentials " +
                    $"for the DynamoDB checkpointer '{name}'.");
            }

            if (hasAccessKey && !string.IsNullOrEmpty(options.ProfileName))
            {
                throw new OrleansConfigurationException(
                    $"Explicit credentials and {nameof(DynamoDBStreamQueueCheckpointerOptions.ProfileName)} cannot both be configured " +
                    $"for the DynamoDB checkpointer '{name}'.");
            }
        }
    }
}

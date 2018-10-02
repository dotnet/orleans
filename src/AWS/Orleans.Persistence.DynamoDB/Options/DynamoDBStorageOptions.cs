﻿using Newtonsoft.Json;
using Orleans.Persistence.DynamoDB;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class DynamoDBStorageOptions
    {
        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment.
        /// </summary>
        public string ServiceId { get; set; } = string.Empty;

        /// <summary>
        /// AccessKey string for DynamoDB Storage
        /// </summary>
        [Redact]
        public string AccessKey { get; set; }

        /// <summary>
        /// Secret key for DynamoDB storage
        /// </summary>
        [Redact]
        public string SecretKey { get; set; }

        /// <summary>
        /// DynamoDB Service name 
        /// </summary>
        public string Service { get; set; }

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
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        public bool UseJson { get; set; }
        public bool UseFullAssemblyNames { get; set; }
        public bool IndentJson { get; set; }
        public TypeNameHandling? TypeNameHandling { get; set; }
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

        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.TableName))
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.TableName)} is not valid.");

            if (this.options.ReadCapacityUnits == 0)
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.ReadCapacityUnits)} is not valid.");

            if (this.options.WriteCapacityUnits == 0)
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.WriteCapacityUnits)} is not valid.");
        }
    }
}

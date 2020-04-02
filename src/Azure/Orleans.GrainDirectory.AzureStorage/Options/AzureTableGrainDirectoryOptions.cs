using System;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Extensions.Options;
using Orleans.GrainDirectory.AzureStorage;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class AzureTableGrainDirectoryOptions
    {
        /// <summary>
        /// Connection string for Azure Storage
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Table name for Azure Storage
        /// </summary>
        public string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "GrainDirectory";
    }

    public class AzureTableGrainDirectoryOptionsValidator : IConfigurationValidator
    {
        private AzureTableGrainDirectoryOptions options;

        public AzureTableGrainDirectoryOptionsValidator(IOptions<AzureTableGrainDirectoryOptions> options)
        {
            this.options = options.Value;
        }

        public void ValidateConfiguration()
        {
            if (!CloudStorageAccount.TryParse(this.options.ConnectionString, out var ignore))
                throw new OrleansConfigurationException(
                    $"Configuration for AzureTableGrainDirectoryOptions is invalid. {nameof(this.options.ConnectionString)} is not valid.");

            try
            {
                AzureTableUtils.ValidateTableName(this.options.TableName);
            }
            catch (Exception ex)
            {
                throw new OrleansConfigurationException(
                    $"Configuration for AzureTableGrainDirectoryOptions is invalid. {nameof(this.options.TableName)} is not valid.", ex);
            }
        }
    }
}

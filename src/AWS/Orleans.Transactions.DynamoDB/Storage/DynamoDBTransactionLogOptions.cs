using Microsoft.Extensions.Options;
using Orleans.Runtime;
using Orleans.Transactions.DynamoDB;

namespace Orleans.Configuration
{
    public class DynamoDBTransactionLogOptions
    {
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
        /// Defaults to 'TransactionLog'.
        /// </summary>
        public string TableName { get; set; } = "TransactionLog";
    }

    public class DynamoDBTransactionLogOptionsValidator : IConfigurationValidator
    {
        private readonly DynamoDBTransactionLogOptions options;

        public DynamoDBTransactionLogOptionsValidator(IOptions<DynamoDBTransactionLogOptions> configurationOptions)
        {
            this.options = configurationOptions.Value;
        }

        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.TableName))
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBTransactionLogStorage is invalid. {nameof(this.options.TableName)} is not valid.");

            if (this.options.ReadCapacityUnits == 0)
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBTransactionLogStorage is invalid. {nameof(this.options.ReadCapacityUnits)} is not valid.");

            if (this.options.WriteCapacityUnits == 0)
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBTransactionLogStorage is invalid. {nameof(this.options.WriteCapacityUnits)} is not valid.");
        }
    }
}

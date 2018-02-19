using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class DynamoDBTransactionLogOptions
    {
        public string ConnectionString { get; set; }

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
            if (string.IsNullOrWhiteSpace(this.options.ConnectionString))
            {
                throw new OrleansConfigurationException($"Invalid DynamoDBTransactionLogOptions. ConnectionString is required.");
            }
            if (string.IsNullOrWhiteSpace(this.options.TableName))
            {
                throw new OrleansConfigurationException($"Invalid DynamoDBTransactionLogOptions. TableName is required.");
            }
        }
    }
}

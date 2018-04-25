
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    public class AzureTransactionLogOptions
    {
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        public string TableName { get; set; } = "TransactionLog";
    }

    public class AzureTransactionLogOptionsValidator : IConfigurationValidator
    {
        private readonly AzureTransactionLogOptions options;

        public AzureTransactionLogOptionsValidator(IOptions<AzureTransactionLogOptions> configurationOptions)
        {
            this.options = configurationOptions.Value;
        }

        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.ConnectionString))
            {
                throw new OrleansConfigurationException($"Invalid AzureTransactionLogOptions. ConnectionString is required.");
            }
            if (string.IsNullOrWhiteSpace(this.options.TableName))
            {
                throw new OrleansConfigurationException($"Invalid AzureTransactionLogOptions. TableName is required.");
            }
        }
    }
}

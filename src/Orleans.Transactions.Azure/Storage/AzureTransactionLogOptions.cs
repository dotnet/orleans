
using Microsoft.Extensions.Options;
using Orleans.Runtime;

namespace Orleans.Transactions.Azure
{
    public class AzureTransactionLogOptions
    {
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
                throw new OrleansConfigurationException($"The azure transaction log was configured with an invalid connection string");
            }
            if (string.IsNullOrWhiteSpace(this.options.TableName))
            {
                throw new OrleansConfigurationException($"The azure transaction log was configured with an invalid table name");
            }
        }
    }
}

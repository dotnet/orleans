
namespace Orleans.Transactions.Azure
{
    public class AzureTransactionLogOptions
    {
        public string ConnectionString { get; set; }

        public string TableName { get; set; } = "TransactionLog";
    }
}


namespace Orleans.Transactions.AzureStorage
{
    public class AzureTransactionLogConfiguration
    {
        public string ConnectionString { get; set; }

        public string TableName { get; set; } = "TransactionLog";

        internal void Copy(AzureTransactionLogConfiguration other)
        {
            if (other == null) Copy(new AzureTransactionLogConfiguration());
            this.ConnectionString = other.ConnectionString;
            this.TableName = other.TableName;
        }
    }
}

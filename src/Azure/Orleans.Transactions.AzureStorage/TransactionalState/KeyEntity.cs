using Microsoft.WindowsAzure.Storage.Table;

namespace Orleans.Transactions.AzureStorage
{
    internal class KeyEntity : TableEntity
    {
        public const string RK = "tsk";

        public KeyEntity()
        {
            this.RowKey = RK;
        }

        public string CommittedTransactionId { get; set; }
        public string Metadata { get; set; }
    }
}

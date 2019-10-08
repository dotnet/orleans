using Microsoft.Azure.Cosmos.Table;

namespace Orleans.Transactions.AzureStorage
{
    internal class KeyEntity : TableEntity
    {
        public const string RK = "k";

        public KeyEntity()
        {
            this.RowKey = RK;
        }

        public long CommittedSequenceId { get; set; }
        public string Metadata { get; set; }
    }
}

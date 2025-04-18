using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions.AdoNet.TransactionalState
{
    /// <summary>
    /// 
    /// </summary>
    public class KeyEntity
    {
        public const string RK = "k";

        public KeyEntity()
        {
            this.RowKey = RK;
        }

        public long CommittedSequenceId { get; set; }
        public string Metadata { get; set; }
        public string StateId { get; set; }// 同PartitionKey
        public DateTimeOffset? Timestamp { get; set; }

        public string ETag { get; set; }

        public string RowKey { get; set; }
    }

}

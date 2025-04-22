using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions.AdoNet.Entity
{
    /// <summary>
    /// 
    /// </summary>
    internal class KeyEntity : IEntity
    {
        public const string RK = "k";

        public KeyEntity()
        {
            RowKey = RK;
        }

        public string ETag { get; set; }
        public string StateId { get; set; }

        public string RowKey { get; set; }

        public DateTimeOffset? Timestamp { get; set; }

        public long CommittedSequenceId { get; set; }
        public string Metadata { get; set; }
    }

}

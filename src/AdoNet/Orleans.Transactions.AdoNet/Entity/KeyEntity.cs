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
        public string StateId { get; set; }

        public DateTimeOffset? Timestamp { get; set; }

        public long CommittedSequenceId { get; set; }
        public string Metadata { get; set; }
        public string ETag { get; set; }
    }
}

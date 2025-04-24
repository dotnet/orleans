using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions.AdoNet.Entity;
internal interface IEntity
{
    public string ETag { get; set; }

    public string StateId { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
}

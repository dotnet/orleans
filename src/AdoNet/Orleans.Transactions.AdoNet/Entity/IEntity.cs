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
    /// <summary>
    /// this is data modify time
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }
}

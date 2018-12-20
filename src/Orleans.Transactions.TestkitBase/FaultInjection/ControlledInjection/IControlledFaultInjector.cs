using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.TestKit
{
    public interface IControlledTransactionFaultInjector : ITransactionFaultInjector
    {
        bool InjectBeforeStore { get; set; }
        bool InjectAfterStore { get; set; }
    }
}

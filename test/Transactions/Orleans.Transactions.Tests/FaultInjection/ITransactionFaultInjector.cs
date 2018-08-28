using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions.Tests.FaultInjection
{
    public interface ITransactionFaultInjector
    {
        void BeforeStore();
        void AfterStore();
    }
}

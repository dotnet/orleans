using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions.TestKit
{
    public interface ITransactionFaultInjector
    {
        void BeforeStore();
        void AfterStore();
    }
}

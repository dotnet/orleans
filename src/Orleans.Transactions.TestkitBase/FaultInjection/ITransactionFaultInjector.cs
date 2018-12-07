using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions.TestKit.Base
{
    public interface ITransactionFaultInjector
    {
        void BeforeStore();
        void AfterStore();
    }
}

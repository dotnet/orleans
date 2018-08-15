using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions
{
    public interface ITransactionalFaultInjector
    {
        void BeforeStore();
        void AfterStore();

    }


}

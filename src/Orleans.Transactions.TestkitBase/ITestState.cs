using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions.TestKit
{
    public interface ITestState
    {
        int state { get; set; }
    }
}

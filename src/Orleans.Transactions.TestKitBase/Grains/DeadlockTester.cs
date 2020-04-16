using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Transactions.DeadlockDetection;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    [StatelessWorker, Reentrant]
    public class DeadlockTester : Grain, IDeadlockTester
    {

    }
}
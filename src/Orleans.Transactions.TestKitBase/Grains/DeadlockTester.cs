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
        private SimpleTransactionalLockObserver monitor;

        public DeadlockTester(ITransactionalLockObserver monitor)
        {
            this.monitor = (SimpleTransactionalLockObserver)monitor;
        }
        public Task<(bool hasCycles, IList<WaitForGraph.Node> cycle, string graphOut)> CheckForCycles()
        {
            return Task.FromResult((false, (IList<WaitForGraph.Node>) Array.Empty<WaitForGraph.Node>(), ""));
        }
    }
}
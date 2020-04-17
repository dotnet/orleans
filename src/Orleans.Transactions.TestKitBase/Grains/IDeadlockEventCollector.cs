using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Transactions.DeadlockDetection;

namespace Orleans.Transactions.TestKit.Base.Grains
{
    [Serializable]
    [Immutable]
    public class DeadlockEvent
    {
        public DateTime StartTime;
        public TimeSpan Duration;
        public bool Local;
        public int RequestCount;
        public bool IsDefinite;
        public LockInfo[] Locks;
        public bool Deadlocked;
    }

    public interface IDeadlockEventCollector : IGrainWithIntegerKey
    {
        [OneWay]
        [Transaction(TransactionOption.Suppress)]
        Task ReportEvent(DeadlockEvent @event);

        [Transaction(TransactionOption.Suppress)]
        Task<IList<DeadlockEvent>> GetEvents();

        [Transaction(TransactionOption.Suppress)]
        Task Clear();
    }
}
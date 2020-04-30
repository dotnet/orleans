using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Transactions.DeadlockDetection
{
    public interface IDeadlockListener
    {
        void DeadlockDetected(IEnumerable<LockInfo> locks, DateTime analysisStartedAt, bool detectedLocally, int requestsToDetection,
            TimeSpan analysisDuration);
        void DeadlockNotDetected(DateTime analysisStartedAt, int requestsMade, TimeSpan analysisDuration,
            bool isDefinite);
    }
}
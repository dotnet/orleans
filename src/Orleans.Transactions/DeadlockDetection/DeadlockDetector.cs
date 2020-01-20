using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.DeadlockDetection
{

    [Reentrant]
    public class DeadlockDetector : Grain, IDeadlockDetector
    {
        private readonly ILogger<DeadlockDetector> logger;
        public DeadlockDetector(ILogger<DeadlockDetector> logger)
        {
            this.logger = logger;
        }

        public async Task CheckForDeadlocks(ParticipantId resourceId, IList<Guid> transactionIds)
        {
            // Right now, this is called multiple times (once for each deadlocked transaction queue).  To avoid
            // this, we might want to  keep track of things we're currently examining and do a fast exit if
            // another call is already on it.

            var txStr = string.Join(",", transactionIds);
            this.logger.LogInformation($"Checking for deadlocks around {resourceId} for {txStr}");

            WaitForGraph wfg = await CollectGraph(resourceId, transactionIds);
            if (!wfg.HasCycles)
            {
                this.logger.LogInformation($"No cycles detected in {wfg}");
                return;
            }

            this.logger.LogInformation($"Deadlock detected in {wfg}");
            foreach (LockInfo lockInfo in wfg.LocksToBreak)
            {
                this.logger.LogInformation($"would abort {lockInfo.TransactionId}");
            }
        }

        private async Task<WaitForGraph> CollectGraph(ParticipantId resource, IList<Guid> lockedBy)
        {

            LockSnapshot localSnapshot =
                ServiceProvider.GetRequiredService<ITransactionalLockObserver>().CreateSnapshot(resource, lockedBy);

            if (localSnapshot.IsLocallyDeadlocked)
            {
                return WaitForGraph.FromLockInfo(localSnapshot.Snapshot);
            }

            var snapshots = await GrainFactory.GetGrain<IManagementGrain>(0)
                .SendControlCommandToProvider(typeof(SimpleTransactionalLockObserver).FullName,
                    SimpleTransactionalLockObserver.ProviderName, 0, new CollectLocksRequest
                    {
                        ResourceId = resource, TransactionIds = lockedBy
                    });

            var locks = new List<LockInfo>();
            foreach (object o in snapshots)
            {
                var snapshot = (LockSnapshot)o;
                if (snapshot.IsLocallyDeadlocked)
                {
                    return WaitForGraph.FromLockInfo(snapshot.Snapshot);
                }

                locks.AddRange(snapshot.Snapshot);
            }

            return WaitForGraph.FromLockInfo(locks);
        }
    }
}
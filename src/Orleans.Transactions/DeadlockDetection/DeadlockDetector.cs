using System;
using System.Collections;
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
        private readonly ITransactionalLockObserver localObserver;

        private readonly IDictionary<Guid, DeadlockDetectionBatch> batches =
            new Dictionary<Guid, DeadlockDetectionBatch>();
        private readonly IDictionary<Guid, Guid> batchesByTransaction = new Dictionary<Guid, Guid>();
        private readonly IDictionary<ParticipantId, Guid> batchesByResource = new Dictionary<ParticipantId, Guid>();

        internal DeadlockDetector(ILogger<DeadlockDetector> logger,
            ITransactionalLockObserver localObserver)
        {
            this.logger = logger;
            this.localObserver = localObserver;
        }

        public async Task CheckForDeadlocks(CollectLocksResponse message)
        {
            if (message.BatchId != null && this.batches.TryGetValue(message.BatchId.Value, out var batch))
            {
                await this.ScheduleBatchUpdate(batch, message);
            }

            // we don't have a batch for this request - lets see if we have any that are interested in
            // First, we have to merge some batches
            await this.MergeOverlappingBatches();

            bool handled = false;
            foreach (var lockInfo in message.Locks)
            {
                if (this.batchesByResource.TryGetValue(lockInfo.Resource, out var batchId) && this.batches.TryGetValue(batchId, out batch))
                {
                    await this.ScheduleBatchUpdate(batch, message);
                    handled = true;
                }

                if (this.batchesByTransaction.TryGetValue(lockInfo.TxId, out batchId) &&
                    this.batches.TryGetValue(batchId, out batch))
                {
                    await this.ScheduleBatchUpdate(batch, message);
                    handled = true;
                }
            }

            if (!handled)
            {
                batch = new DeadlockDetectionBatch();
                this.batches[batch.Id] = batch;
                await this.ScheduleBatchUpdate(batch, message);
            }
        }

        private Task MergeOverlappingBatches() => throw new NotImplementedException();

        private Task ScheduleBatchUpdate(DeadlockDetectionBatch batch, CollectLocksResponse message) => throw new NotImplementedException();
    }
}
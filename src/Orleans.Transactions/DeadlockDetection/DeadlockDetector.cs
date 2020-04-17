using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;
using Orleans.Timers.Internal;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.DeadlockDetection
{

    [Reentrant]
    public class DeadlockDetector : Grain, IDeadlockDetector
    {

        private enum SiloStatus
        {
            BeforeRequest, WaitingForLocks, ReceivedLocks, Dead
        }

        private enum EndBatchReason
        {
            OutOfRequests, Stable, Deadlocked
        }

        private class SiloInfo
        {
            public SiloAddress Address { get; }
            public SiloStatus Status { get; set; } = SiloStatus.BeforeRequest;
            public DateTime RequestDeadline { get; set; }

            public long? MaxVersion { get; set; }

            public ISet<ParticipantId> Grains { get; } = new HashSet<ParticipantId>();

            public SiloInfo(SiloAddress address)
            {
                this.Address = address;
            }
        }

        private class Batch
        {
            public Guid Id { get; }
            public Dictionary<SiloAddress, SiloInfo> SiloInfos { get; }

            public WaitForGraph WaitForGraph { get; set; }
            public bool Changed { get; set; }
            public ISet<Guid> KnownTransactions { get; } = new HashSet<Guid>();
            public ISet<Guid> NewTransactions { get; } = new HashSet<Guid>();

            public int RequestCount { get; set; }

            public Batch(IEnumerable<SiloAddress> silos, DateTime analysisStartTime)
            {
                this.Id = Guid.NewGuid();
                this.AnalysisStartTime = analysisStartTime;
                this.SiloInfos = new Dictionary<SiloAddress, SiloInfo>();
                foreach (var address in silos)
                {
                    this.SiloInfos[address] = new SiloInfo(address);
                }
            }

            public DateTime AnalysisStartTime { get; }
        }

        // TODO deadlock options
        private int MaxRequestCount { get; } = 5;
        private TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(2);

        private ISiloStatusOracle siloStatusOracle;
        private readonly ILogger<DeadlockDetector> logger;
        private readonly IDictionary<Guid, Batch> batches =
            new Dictionary<Guid, Batch>();

        private IInternalGrainFactory internalGrainFactory;
        private IDeadlockListener[] deadlockListeners;

        public DeadlockDetector(ILogger<DeadlockDetector> logger)
        {
            this.logger = logger;
        }

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();
            this.siloStatusOracle = this.ServiceProvider.GetRequiredService<ISiloStatusOracle>();
            this.internalGrainFactory = this.ServiceProvider.GetRequiredService<IInternalGrainFactory>();
            this.deadlockListeners = this.ServiceProvider.GetServices<IDeadlockListener>().ToArray();
        }

        public async Task CheckForDeadlocks(CollectLocksResponse message)
        {
            // TODO LOG  this.logger.LogInformation($"CheckForDeadlocks({message.BatchId},{message.SiloAddress},{message.MaxVersion})");
            Batch batch;
            if (message.BatchId == null)
            {
                // new batch - TODO rate limit this
                batch = new Batch(this.GetSiloAddresses(), DateTime.UtcNow);
                this.batches[batch.Id] = batch;
            }
            else if(!this.batches.TryGetValue(message.BatchId.Value, out batch))
            {
               // TODO LOG  if(this.logger.IsEnabled(LogLevel.Trace)){ this.logger.LogInformation($"received message for missing batch {message}"); }
               return;
            }

            await this.UpdateBatch(batch, message);
        }

        private async Task UpdateBatch(Batch batch, CollectLocksResponse message)
        {
            if (!batch.SiloInfos.TryGetValue(message.SiloAddress, out var siloInfo))
            {
                this.logger.LogWarning(
                    "Got a collect locks request for a silo that didn't exist when detection started: {SiloAddress}",
                    message.SiloAddress);
                return;
            }

            if (siloInfo.Status == SiloStatus.Dead)
            {
                this.logger.LogWarning("Silo {SiloAddress} responded {MsLate}ms late",
                    message.SiloAddress, (DateTime.UtcNow - siloInfo.RequestDeadline).TotalMilliseconds);
            }
            else if (siloInfo.Status == SiloStatus.ReceivedLocks)
            {
                this.logger.LogWarning("Silo {SiloAddress} sent locks twice", message.SiloAddress);
            }

            if (message.MaxVersion != null)
            {
                siloInfo.MaxVersion = Math.Max(message.MaxVersion.Value, siloInfo.MaxVersion.GetValueOrDefault(0));
            }

            WaitForGraph updatedGraph;
            bool graphChanged;
            if (batch.WaitForGraph == null)
            {
                updatedGraph = new WaitForGraph(message.Locks);
                graphChanged = true;
            }
            else
            {
                graphChanged = batch.WaitForGraph.MergeWith(message.Locks, out updatedGraph);
            }

            if(graphChanged)
            {
                batch.WaitForGraph = updatedGraph;
                if (batch.WaitForGraph.DetectCycles(out var cycle))
                {
                    await this.BreakLocks(batch,  cycle);
                    return;
                }
            }

            foreach (var lockInfo in message.Locks)
            {
                if (batch.KnownTransactions.Add(lockInfo.TxId))
                {
                    batch.NewTransactions.Add(lockInfo.TxId);
                }

                siloInfo.Grains.Add(lockInfo.Resource);
            }

            bool readyForMore = true;
            foreach (var silo in batch.SiloInfos.Values)
            {
                if (silo.Status == SiloStatus.Dead || silo.Status == SiloStatus.ReceivedLocks)
                {
                    continue;
                }

                readyForMore = false;
            }

            if (readyForMore)
            {
                batch.RequestCount++;
                if (batch.RequestCount >= this.MaxRequestCount)
                {
                    this.EndBatch(batch, EndBatchReason.OutOfRequests);
                    return;
                }

                if (batch.NewTransactions.Count == 0)
                {
                    this.EndBatch(batch, EndBatchReason.Stable);
                    return;
                }

                var newTransactions = batch.NewTransactions.ToArray();
                batch.NewTransactions.Clear();
                foreach (var silo in batch.SiloInfos.Values)
                {
                    if (silo.Status == SiloStatus.Dead) continue;
                    silo.Status = SiloStatus.WaitingForLocks;
                    this.RequestLocksFromSilo(batch, silo, newTransactions);
                }
            }
            else
            {
                // make sure any silos in the BeforeRequests state go into the waiting for locks state
                foreach (var silo in batch.SiloInfos.Values)
                {
                    if (silo.Status == SiloStatus.BeforeRequest)
                    {
                        // Send all known transactions, because we have no idea what we can learn from this silo
                        this.RequestLocksFromSilo(batch, silo, batch.KnownTransactions);
                    }
                }
            }
        }

        private Task BreakLocks(Batch batch, IEnumerable<LockInfo> cycle)
        {
            var locks = cycle.ToArray();
            this.NotifyDeadlockDetected(batch, locks);
            this.batches.Remove(batch.Id);
            return locks.BreakLocks();
        }

        private void EndBatch(Batch batch, EndBatchReason reason)
        {
            this.NotifyDetectionFailed(batch, reason);
            this.batches.Remove(batch.Id);
        }

        private void NotifyDeadlockDetected(Batch batch, IEnumerable<LockInfo> cycle) =>
            this.RunListeners(l => l.DeadlockDetected(cycle, batch.AnalysisStartTime, false, batch.RequestCount,
                DateTime.UtcNow - batch.AnalysisStartTime));

        private void NotifyDetectionFailed(Batch batch, EndBatchReason reason) =>
            this.RunListeners(l => l.DeadlockNotDetected(batch.AnalysisStartTime, batch.RequestCount,
                DateTime.UtcNow - batch.AnalysisStartTime, reason == EndBatchReason.Stable));

        private void RunListeners(Action<IDeadlockListener> action)
        {
            for (var i = 0; i < this.deadlockListeners.Length; i++)
            {
                var listener = this.deadlockListeners[i];
                if (listener == null) continue;
                try
                {
                    action(listener);
                }
                catch (Exception e)
                {
                    this.logger.LogError(e, "Error notifying global deadlock listener {listener}, will be removed", listener);
                    this.deadlockListeners[i] = null;
                }
            }
        }

        private void RequestLocksFromSilo(Batch batch, SiloInfo silo, IEnumerable<Guid> transactions)
        {
            silo.Status = SiloStatus.WaitingForLocks;
            silo.RequestDeadline = DateTime.UtcNow + this.RequestTimeout;
            var lockObserver =
                this.internalGrainFactory.GetSystemTarget<ILocalDeadlockDetector>(
                    Constants.LocalDeadlockDetectorId, silo.Address);
            lockObserver.CollectLocks(new CollectLocksRequest
            {
                BatchId = batch.Id, MaxVersion = silo.MaxVersion, TransactionIds = transactions.ToList()
            }).Ignore();
        }

        private IEnumerable<SiloAddress> GetSiloAddresses() => this.siloStatusOracle.GetApproximateSiloStatuses(true).Keys;
    }
}
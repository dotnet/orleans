using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
        private readonly DeadlockDetectionOptions options;

        public DeadlockDetector(ILogger<DeadlockDetector> logger, IOptions<DeadlockDetectionOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
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
                if (this.StartNewBatch(message, out batch))
                {
                    this.batches[batch.Id] = batch;
                }
                else
                {
                    // TODO log rate limited
                    return;
                }
            }
            else if(!this.batches.TryGetValue(message.BatchId.Value, out batch))
            {
               // TODO LOG  if(this.logger.IsEnabled(LogLevel.Trace)){ this.logger.LogInformation($"received message for missing batch {message}"); }
               return;
            }

            await this.UpdateBatch(batch, message);
        }

        private bool StartNewBatch(CollectLocksResponse message, out Batch batch)
        {
            if (this.batches.Count >= this.options.MaxConcurrentDeadlockAnalysis)
            {
                // TODO report?
                this.logger.LogWarning("Too many batches exist, discarding a message");
                batch = null;
                return false;
            }

            // look for a batch that intersects the grains / transactions in this message
            // before starting a new one
            var transactionsInMessage = message.Locks.Select(l => l.TxId).ToSet();
            foreach (var existingBatch in this.batches.Values)
            {
                this.logger.LogInformation(
                    $"checking for overlap between messageTxs {string.Join(",", transactionsInMessage)}" +
                    $" and batch tx {string.Join(",", existingBatch.KnownTransactions)}");
                if (existingBatch.KnownTransactions.Overlaps(transactionsInMessage))
                {
                    this.logger.LogInformation("joined existing batch");
                    batch = existingBatch;
                    return true;
                }
            }

            this.logger.LogInformation("no existing batch found - starting a new one");
            // we're starting a new one
            batch = new Batch(this.GetSiloAddresses(), DateTime.UtcNow);
            return true;
        }

        private Task UpdateBatch(Batch batch, CollectLocksResponse message)
        {
            if (!batch.SiloInfos.TryGetValue(message.SiloAddress, out var siloInfo))
            {
                this.logger.LogWarning(
                    "Got a collect locks request for a silo that didn't exist when detection started: {SiloAddress}",
                    message.SiloAddress);
                return Task.CompletedTask;
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
                if (batch.WaitForGraph.DetectCycles(out var cycles))
                {
                    var tasks = cycles.Select(c => this.BreakLocks(batch, c));
                    return Task.WhenAll(tasks);
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
                if (batch.RequestCount >= this.options.MaxDeadlockRequests)
                {
                    this.EndBatch(batch, EndBatchReason.OutOfRequests);
                    return Task.CompletedTask;
                }

                if (batch.NewTransactions.Count == 0)
                {
                    this.EndBatch(batch, EndBatchReason.Stable);
                    return Task.CompletedTask;
                }

                var newTransactions = batch.NewTransactions.ToArray();
                batch.NewTransactions.Clear();
                foreach (var silo in batch.SiloInfos.Values)
                {
                    if (silo.Status != SiloStatus.Dead && silo.RequestDeadline < DateTime.UtcNow)
                    {
                        silo.Status = SiloStatus.Dead;
                    }
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

            return Task.CompletedTask;
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
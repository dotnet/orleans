using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.DeadlockDetection
{
    internal class DeadlockDetectionLockObserver : SystemTarget, ITransactionalLockObserver, ILocalDeadlockDetector
    {
        private long currentVersion = 0L;

        private readonly LockTracker lockTracker = new LockTracker();

        private readonly ILogger<DeadlockDetectionLockObserver> logger;

        private readonly IGrainFactory grainFactory;

        private readonly IGrainRuntime runtime;

        public DeadlockDetectionLockObserver(IMessageCenter messageCenter, ILoggerFactory loggerFactory,
            IGrainFactory grainFactory, IGrainRuntime runtime) :
            base(Constants.LocalDeadlockDetectorId, messageCenter.MyAddress, loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<DeadlockDetectionLockObserver>();
            this.grainFactory = grainFactory;
            this.runtime = runtime;
        }

        public void OnResourceRequested(Guid transactionId, ParticipantId resourceId)
        {
            this.logger.LogInformation($"WAIT: {transactionId} for {resourceId}");
            this.lockTracker.TrackWait(resourceId, transactionId, this.currentVersion);
        }

        public void OnResourceLocked(Guid transactionId, ParticipantId resourceId, bool isReadOnly)
        {
            this.logger.LogInformation($"LOCK: {transactionId} on {resourceId} (ro={isReadOnly})");
            this.lockTracker.TrackEnterLock(resourceId, transactionId, this.currentVersion);
        }

        public void OnResourceUnlocked(Guid transactionId, ParticipantId resourceId)
        {
            this.logger.LogInformation($"UNLOCK: {transactionId} on {resourceId}");
            this.lockTracker.TrackExitLock(resourceId, transactionId);
        }

        public async Task StartDeadlockDetection(ParticipantId resource, IEnumerable<Guid> lockedBy)
        {
            // Because we don't actually await this call (to avoid messing up transactional state on an error), we wrap it in
            // a try catch.
            try
            {
                var localGraph =
                    new WaitForGraph(this.lockTracker.GetLocks()).GetConnectedSubGraph(lockedBy, new[] {resource});
                if (localGraph.DetectCycles(out var cycle))
                {
                    this.logger.LogInformation($"found a local cycle: {string.Join(",", cycle)}");
                    var tasks = cycle.Where(l => !l.IsWait).Select(l =>
                        l.Resource.Reference.AsReference<ITransactionalResourceExtension>()
                            .BreakLocks(l.Resource.Name));
                    await Task.WhenAll(tasks);
                    this.logger.LogInformation("broke the locks?");
                }
                else
                {
                    await this.grainFactory.GetGrain<IDeadlockDetector>(0).CheckForDeadlocks(new CollectLocksResponse
                    {
                        Locks = localGraph.ToLockKeys(),
                        BatchId = null,
                        MaxVersion = null,
                        SiloAddress = this.runtime.SiloAddress
                    });
                }

            }
            catch (Exception e)
            {
                this.logger.LogError(e, "deadlock detection threw an exception");
            }
        }

        private long IncrementMaxVersion() => Interlocked.Increment(ref this.currentVersion);

        public async Task CollectLocks(CollectLocksRequest request)
        {
            long responseMaxVersion;
            if (request.MaxVersion == null)
            {
                responseMaxVersion = this.IncrementMaxVersion();
            }
            else
            {
                responseMaxVersion = request.MaxVersion.Value;
            }

            var snapshot = this.lockTracker.GetLocks(request.MaxVersion);
            var wfg = new WaitForGraph(snapshot).GetConnectedSubGraph(request.TransactionIds, Enumerable.Empty<ParticipantId>());

            await this.grainFactory.GetGrain<IDeadlockDetector>(0).CheckForDeadlocks(new CollectLocksResponse
            {
                BatchId = request.BatchId,
                Locks = wfg.ToLockKeys(),
                MaxVersion = responseMaxVersion,
                SiloAddress = this.runtime.SiloAddress
            });
        }

    }
}
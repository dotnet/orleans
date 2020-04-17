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

        private readonly IDeadlockListener[] deadlockListeners;

        public DeadlockDetectionLockObserver(IMessageCenter messageCenter, ILoggerFactory loggerFactory,
            IGrainFactory grainFactory, IGrainRuntime runtime, IServiceProvider serviceProvider) :
            base(Constants.LocalDeadlockDetectorId, messageCenter.MyAddress, loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<DeadlockDetectionLockObserver>();
            this.grainFactory = grainFactory;
            this.runtime = runtime;
            this.deadlockListeners = serviceProvider.GetServices<IDeadlockListener>().ToArray();
        }

        public void OnResourceRequested(Guid transactionId, ParticipantId resourceId) =>
            this.lockTracker.TrackWait(resourceId, transactionId, this.currentVersion);

        public void OnResourceLocked(Guid transactionId, ParticipantId resourceId) =>
            this.lockTracker.TrackEnterLock(resourceId, transactionId, this.currentVersion);

        public void OnResourceUnlocked(Guid transactionId, ParticipantId resourceId) =>
            this.lockTracker.TrackExitLock(resourceId, transactionId);

        public async Task StartDeadlockDetection(ParticipantId resource, IEnumerable<Guid> lockedBy)
        {
            // Because we don't actually await this call (to avoid messing up transactional state on an error), we wrap it in
            // a try catch.
            try
            {
                var startTime = DateTime.UtcNow;
                var localGraph =
                    new WaitForGraph(this.lockTracker.GetLocks()).GetConnectedSubGraph(lockedBy, new[] {resource});
                if (localGraph.DetectCycles(out var cycle))
                {
                    await cycle.BreakLocks();
                    NotifyDeadlockListeners(startTime, DateTime.UtcNow, cycle);
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

        private void NotifyDeadlockListeners(DateTime startTime, DateTime now, IList<LockInfo> locksInCycle)
        {
            for(var i=0; i < this.deadlockListeners.Length; i ++)
            {
                var listener = this.deadlockListeners[i];
                if (listener == null) continue;
                try
                {
                    listener.DeadlockDetected(locksInCycle, startTime, true, 0, now - startTime);
                }
                catch (Exception e)
                {
                    this.logger.LogError(e, $"Error while notifying local deadlock listener {listener}.  It will be removed");
                    this.deadlockListeners[i] = null; // Not sure about removing them, but seems safer for now
                }
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
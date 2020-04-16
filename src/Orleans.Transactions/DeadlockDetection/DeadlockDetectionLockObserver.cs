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

namespace Orleans.Transactions.DeadlockDetection
{
    internal class DeadlockDetectionLockObserver : SystemTarget, ITransactionalLockObserver
    {
        public const string ProviderName = nameof(DeadlockDetectionLockObserver);

        public static IControllable Create(IServiceProvider sp, string providerName) =>
            ActivatorUtilities.CreateInstance<DeadlockDetectionLockObserver>(sp);

        public Task<object> ExecuteCommand(int command, object arg)
        {
            var request = (CollectLocksRequest)arg;
            return Task.FromResult<object>(null);
        }

        private long currentVersion = 0L;

        private readonly LockTracker lockTracker = new LockTracker();

        private readonly ILogger<DeadlockDetectionLockObserver> logger;

        private readonly IGrainFactory grainFactory;

        private readonly IGrainRuntime runtime;

        public DeadlockDetectionLockObserver(ILogger<DeadlockDetectionLockObserver> logger,
            IGrainFactory grainFactory, IGrainRuntime runtime)
        {
            this.logger = logger;
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
            var localGraph = new WaitForGraph(this.lockTracker.GetLocks()).GetConnectedSubGraph(lockedBy, new[]{ resource });
            if (localGraph.DetectCycles(out var cycle))
            {
                // blow it up locally
            }
            else
            {
                await grainFactory.GetGrain<IDeadlockDetector>(0).CheckForDeadlocks(new CollectLocksResponse
                {
                    Locks = localGraph.ToLockKeys(),
                    BatchId = null,
                    MaxVersion = null,
                    SiloAddress = this.runtime.SiloAddress
                });
            }
        }

        private long IncrementMaxVersion() => Interlocked.Increment(ref this.currentVersion);

        internal Task<CollectLocksResponse> CollectLocks(CollectLocksRequest request)
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

            return Task.FromResult(new CollectLocksResponse
            {
                BatchId = request.BatchId,
                Locks = wfg.ToLockKeys(),
                MaxVersion = responseMaxVersion,
                SiloAddress = this.runtime.SiloAddress
            });
        }

    }
}
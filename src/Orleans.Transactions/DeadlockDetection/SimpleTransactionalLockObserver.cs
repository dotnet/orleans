using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Providers;

namespace Orleans.Transactions.DeadlockDetection
{
    public class SimpleTransactionalLockObserver : ITransactionalLockObserver
    {
        public const string ProviderName = nameof(SimpleTransactionalLockObserver);

        public static IControllable Create(IServiceProvider sp, string providerName)
        {
            return ActivatorUtilities.CreateInstance<SimpleTransactionalLockObserver>(sp);
        }

        public Task<object> ExecuteCommand(int command, object arg)
        {
            var request = (CollectLocksRequest)arg;
            return Task.FromResult<object>(this.CreateSnapshot(request.ResourceId, request.TransactionIds));
        }

        // TODO we use a "lock everything" approach right now, but we should be able to do something finer grained
        private readonly object syncRoot = new object();

        private readonly Dictionary<Guid, HashSet<ParticipantId>> lockHolders =
            new Dictionary<Guid, HashSet<ParticipantId>>();

        private readonly Dictionary<ParticipantId, HashSet<Guid>> lockWaiters =
            new Dictionary<ParticipantId, HashSet<Guid>>();

        private readonly ILogger<SimpleTransactionalLockObserver> logger;

        private readonly IGrainFactory grainFactory;

        public SimpleTransactionalLockObserver(ILogger<SimpleTransactionalLockObserver> logger, IGrainFactory grainFactory)
        {
            this.logger = logger;
            this.grainFactory = grainFactory;
        }

        public IDisposable OnResourceRequested(Guid transactionId, ParticipantId resourceId)
        {
            this.logger.LogInformation($"WAIT: {transactionId} for {resourceId}");

            lock (this.syncRoot)
            {
                if (!this.lockWaiters.TryGetValue(resourceId, out var waiters))
                {
                    waiters = this.lockWaiters[resourceId] = new HashSet<Guid>();
                }

                waiters.Add(transactionId);
            }

            return Unlocker.ForWait(this, transactionId, resourceId);
        }

        public void OnResourceRequestCancelled(Guid transactionId, ParticipantId resourceId)
        {
            lock (this.syncRoot)
            {
                if (!this.lockWaiters.TryGetValue(resourceId, out var waiters))
                {
                    return;
                }

                if (waiters.Remove(transactionId) && waiters.Count == 0)
                {
                    this.lockWaiters.Remove(resourceId);
                }
            }
        }

        public IDisposable OnResourceLocked(Guid transactionId, ParticipantId resourceId, bool isReadOnly)
        {
            OnResourceRequestCancelled(transactionId, resourceId);
            this.logger.LogInformation($"LOCK: {transactionId} on {resourceId} (ro={isReadOnly})");
            lock (this.syncRoot)
            {
                if (!this.lockHolders.TryGetValue(transactionId, out var locked))
                {
                    locked = this.lockHolders[transactionId] = new HashSet<ParticipantId>();
                }

                locked.Add(resourceId);
            }
            return Unlocker.ForLock(this, transactionId, resourceId);
        }

        public void OnResourceUnlocked(Guid transactionId, ParticipantId resourceId)
        {
            this.logger.LogInformation($"UNLOCK: {transactionId} on {resourceId}");
            lock (this.syncRoot)
            {
                if (this.lockHolders.TryGetValue(transactionId, out var held))
                {
                    if (held.Remove(resourceId) && held.Count == 0)
                    {
                        this.lockHolders.Remove(transactionId);
                    }
                }
            }
        }

        public Task StartDeadlockDetection(ParticipantId resource, IEnumerable<Guid> lockedBy)
        {
            var detector = this.grainFactory.GetGrain<IDeadlockDetector>(0);
            // TODO DEADLOCK We can think more about passing in specific transactions to check, but we actually don't use focused
            // TODO deadlock detection yet, so anything is fine.
            detector.CheckForDeadlocks(resource, lockedBy.ToList()).Ignore();
            return Task.CompletedTask;
        }


        public LockSnapshot CreateSnapshot(ParticipantId resource, IEnumerable<Guid> transactions)
        {
            var snapshot = new LockSnapshot();
            var next = new Stack<WaitForGraph.Node>();
            var visited = new HashSet<WaitForGraph.Node>();
            next.Push(WaitForGraph.Node.ForResource(resource));

            // reuse 'visited' here to make sure we don't add duplicates
            foreach (var tx in transactions)
            {
                if (visited.Add(WaitForGraph.Node.ForTransaction(tx)))
                {
                    next.Push(WaitForGraph.Node.ForTransaction(tx));
                }
            }
            visited.Clear();

            while (next.Count != 0)
            {
                WaitForGraph.Node node = next.Pop();
                visited.Add(node);
                if (node.IsResource)
                {
                    foreach (Guid txId in GetWaiters(node.ResourceId))
                    {
                        WaitForGraph.Node adj = WaitForGraph.Node.ForTransaction(txId);
                        snapshot.Snapshot.Add(new LockInfo{ IsWait = true, ResourceId = node.ResourceId, TransactionId = txId});
                        if (visited.Contains(adj))
                        {
                            // at this point, we know there's a cycle in snapshot.  We can bail early.
                            logger.LogInformation($"found a local deadlock for tx {adj.TransactionId}");
                            snapshot.IsLocallyDeadlocked = true;
                            return snapshot;
                        }
                        next.Push(adj);
                    }
                }
                else
                {
                    foreach (ParticipantId resId in GetLockedResources(node.TransactionId))
                    {
                        WaitForGraph.Node adj = WaitForGraph.Node.ForResource(resId);
                        snapshot.Snapshot.Add(new LockInfo{ IsWait = false, ResourceId = resId, TransactionId = node.TransactionId});
                        if (visited.Contains(adj))
                        {
                            // at this point, we know there's a cycle in snapshot.  We can bail early.
                            logger.LogInformation($"found a local deadlock for tx {adj.TransactionId}");
                            snapshot.IsLocallyDeadlocked = true;
                            return snapshot;
                        }
                        next.Push(adj);
                    }
                }
            }

            return snapshot;
        }

        private ICollection<ParticipantId> GetLockedResources(Guid tx)
        {
            lock (this.syncRoot)
            {
                if (this.lockHolders.TryGetValue(tx, out var locked))
                {
                    return locked.ToList();
                }
            }

            // TODO const
            return new ParticipantId[0];
        }

        private ICollection<Guid> GetWaiters(ParticipantId resource)
        {
            lock (this.syncRoot)
            {
                if (this.lockWaiters.TryGetValue(resource, out var waiters))
                {
                    return waiters.ToList();
                }
            }

            // TODO const
            return new Guid[0];
        }


        private readonly struct Unlocker : IDisposable
        {
            private readonly ITransactionalLockObserver owner;
            public readonly Guid TransactionId;
            public readonly ParticipantId ResourceId;
            public readonly bool IsLocked;

            public static Unlocker ForWait(ITransactionalLockObserver owner, Guid transactionId, ParticipantId resource) =>
                new Unlocker(owner, transactionId, resource, false);
            public static Unlocker ForLock(ITransactionalLockObserver owner, Guid transactionId, ParticipantId resource) =>
                new Unlocker(owner, transactionId, resource, true);

            private Unlocker(ITransactionalLockObserver owner, Guid transactionId, ParticipantId resourceId, bool isLocked)
            {
                this.TransactionId = transactionId;
                this.ResourceId = resourceId;
                this.IsLocked = isLocked;
                this.owner = owner;
            }

            public void Dispose()
            {
                if(this.IsLocked) this.owner.OnResourceUnlocked(this.TransactionId, this.ResourceId);
                else this.owner.OnResourceRequestCancelled(this.TransactionId, this.ResourceId);
            }
        }
    }
}
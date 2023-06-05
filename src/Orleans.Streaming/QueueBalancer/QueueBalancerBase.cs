using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Streams
{
    /// <summary>
    /// Base class for StreamQueueBalancer
    /// </summary>
    public abstract class QueueBalancerBase : IStreamQueueBalancer
    {
        private readonly IAsyncEnumerable<ClusterMembershipSnapshot> clusterMembershipUpdates;
        private readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        private readonly CancellationTokenSource cts;

        protected CancellationToken Cancellation => cts.Token;

        protected SiloAddress SiloAddress { get; }

        protected ILogger Logger { get; }

        protected QueueBalancerBase(IServiceProvider sp, ILogger logger)
            : this(sp.GetRequiredService<IClusterMembershipService>(), sp.GetRequiredService<ILocalSiloDetails>(), logger)
        {
        }

        /// <summary>
        /// This should be primary constructor once IAsyncEnumerable is released
        /// </summary>
        private QueueBalancerBase(IClusterMembershipService clusterMembership, ILocalSiloDetails localSiloDetails, ILogger logger)
        {
            clusterMembershipUpdates = clusterMembership.MembershipUpdates;
            SiloAddress = localSiloDetails.SiloAddress;
            Logger = logger;
            queueBalanceListeners = new List<IStreamQueueBalanceListener>();
            cts = new CancellationTokenSource();
        }

        /// <inheritdoc/>
        public abstract IEnumerable<QueueId> GetMyQueues();

        /// <inheritdoc/>
        public virtual Task Initialize(IStreamQueueMapper queueMapper)
        {
            ListenForClusterChanges().Ignore();
            return Task.CompletedTask;
        }

        public virtual Task Shutdown()
        {
            cts.Cancel(throwOnFirstException: false);
            return Task.CompletedTask;
        }

        #region Queue change notification - replace with IAsyncEnumerable change feed - jbragg

        /// <inheritdoc/>
        public bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }
            lock (queueBalanceListeners)
            {
                if (queueBalanceListeners.Contains(observer))
                {
                    return false;
                }
                queueBalanceListeners.Add(observer);
                return true;
            }
        }
        /// <inheritdoc/>
        public bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            if (observer == null)
            {
                throw new ArgumentNullException(nameof(observer));
            }
            lock (queueBalanceListeners)
            {
                return queueBalanceListeners.Remove(observer);
            }
        }

        protected Task NotifyListeners()
        {
            if (Cancellation.IsCancellationRequested) return Task.CompletedTask;
            List<IStreamQueueBalanceListener> queueBalanceListenersCopy;
            lock (queueBalanceListeners)
            {
                queueBalanceListenersCopy = queueBalanceListeners.ToList(); // make copy
            }
            return Task.WhenAll(queueBalanceListenersCopy.Select(listener => listener.QueueDistributionChangeNotification()));
        }
#endregion

        private async Task ListenForClusterChanges()
        {
            var current = new HashSet<SiloAddress>();
            await foreach (var membershipSnapshot in clusterMembershipUpdates.WithCancellation(Cancellation))
            {
                // get active members
                var update = new HashSet<SiloAddress>(membershipSnapshot.Members.Values
                    .Where(member => member.Status == SiloStatus.Active)
                    .Select(member => member.SiloAddress));

                // if active list has changed, track new list and notify
                if(!current.SetEquals(update))
                {
                    current = update;
                    OnClusterMembershipChange(current);
                }
            }
        }

        protected abstract void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos);
    }
}

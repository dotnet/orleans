using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Internal;

namespace Orleans.Streams
{
    /// <summary>
    /// Base class for StreamQueueBalancer
    /// </summary>
    public abstract partial class QueueBalancerBase : IStreamQueueBalancer
    {
        private readonly IAsyncEnumerable<ClusterMembershipSnapshot> clusterMembershipUpdates;
        private readonly List<IStreamQueueBalanceListener> queueBalanceListeners;
        private readonly CancellationTokenSource cts;
        private Task _listenForClusterChangesTask;

        protected CancellationToken Cancellation => this.cts.Token;

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
            this.clusterMembershipUpdates = clusterMembership.MembershipUpdates;
            this.SiloAddress = localSiloDetails.SiloAddress;
            this.Logger = logger;
            this.queueBalanceListeners = new List<IStreamQueueBalanceListener>();
            this.cts = new CancellationTokenSource();
        }

        /// <inheritdoc/>
        public abstract IEnumerable<QueueId> GetMyQueues();

        /// <inheritdoc/>
        public virtual Task Initialize(IStreamQueueMapper queueMapper)
        {
            using var _ = new ExecutionContextSuppressor();
            _listenForClusterChangesTask = ListenForClusterChanges();
            return Task.CompletedTask;
        }

        public virtual async Task Shutdown()
        {
            try
            {
                this.cts.Cancel(throwOnFirstException: false);
            }
            catch (Exception exc)
            {
                LogErrorSignalingShutdownToken(Logger, exc);
            }

            await _listenForClusterChangesTask.SuppressThrowing();
        }

        /// <inheritdoc/>
        public bool SubscribeToQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (this.queueBalanceListeners)
            {
                if (this.queueBalanceListeners.Contains(observer))
                {
                    return false;
                }
                this.queueBalanceListeners.Add(observer);
                return true;
            }
        }
        /// <inheritdoc/>
        public bool UnSubscribeFromQueueDistributionChangeEvents(IStreamQueueBalanceListener observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (this.queueBalanceListeners)
            {
                return this.queueBalanceListeners.Remove(observer);
            }
        }

        protected Task NotifyListeners()
        {
            if (this.Cancellation.IsCancellationRequested) return Task.CompletedTask;
            List<IStreamQueueBalanceListener> queueBalanceListenersCopy;
            lock (queueBalanceListeners)
            {
                queueBalanceListenersCopy = queueBalanceListeners.ToList(); // make copy
            }
            return Task.WhenAll(queueBalanceListenersCopy.Select(listener => listener.QueueDistributionChangeNotification()));
        }

        private async Task ListenForClusterChanges()
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            var current = new HashSet<SiloAddress>();
            await foreach (var membershipSnapshot in this.clusterMembershipUpdates.WithCancellation(this.Cancellation))
            {
                try
                {
                    // Get active members.
                    var update = new HashSet<SiloAddress>(membershipSnapshot.Members.Values
                        .Where(member => member.Status == SiloStatus.Active)
                        .Select(member => member.SiloAddress));

                    // If active list has changed, track new list and notify.
                    if (!current.SetEquals(update))
                    {
                        current = update;
                        OnClusterMembershipChange(current);
                    }
                }
                catch (Exception exception)
                {
                    LogErrorProcessingClusterMembershipUpdate(Logger, exception);
                }
            }
        }

        protected abstract void OnClusterMembershipChange(HashSet<SiloAddress> activeSilos);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error signaling shutdown token."
        )]
        private static partial void LogErrorSignalingShutdownToken(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Error processing cluster membership update."
        )]
        private static partial void LogErrorProcessingClusterMembershipUpdate(ILogger logger, Exception exception);
    }
}

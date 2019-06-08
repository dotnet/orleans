using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Threading;

namespace Orleans.Runtime.MembershipService
{
    internal class SiloStatusListenerManager : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ConcurrentDictionary<ISiloStatusListener, ISiloStatusListener> listeners
            = new ConcurrentDictionary<ISiloStatusListener, ISiloStatusListener>(ReferenceEqualsComparer<ISiloStatusListener>.Instance);
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly MembershipTableManager membershipTableManager;
        private readonly ILogger<SiloStatusListenerManager> log;
        private readonly IFatalErrorHandler fatalErrorHandler;

        public SiloStatusListenerManager(
            MembershipTableManager membershipTableManager,
            ILogger<SiloStatusListenerManager> log,
            IFatalErrorHandler fatalErrorHandler)
        {
            this.membershipTableManager = membershipTableManager;
            this.log = log;
            this.fatalErrorHandler = fatalErrorHandler;
        }

        public bool Subscribe(ISiloStatusListener listener) => this.listeners.TryAdd(listener, listener);

        public bool Unsubscribe(ISiloStatusListener listener) => this.listeners.TryRemove(listener, out _);

        private async Task ProcessMembershipUpdates()
        {
            var cancellationTask = this.cancellation.Token.WhenCancelled();
            var current = this.membershipTableManager.MembershipTableUpdates;
            ClusterMembershipSnapshot previous = default;
            if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting to process membership updates");
            try
            {
                while (!this.cancellation.IsCancellationRequested)
                {
                    if (previous == default || !current.HasValue)
                    {
                        var next = current.NextAsync();

                        // Handle graceful termination.
                        var task = await Task.WhenAny(next, cancellationTask);
                        if (ReferenceEquals(task, cancellationTask)) break;

                        current = next.GetAwaiter().GetResult();
                    }

                    if (!current.HasValue)
                    {
                        this.log.LogWarning("Received a membership update with no data");
                        continue;
                    }

                    var snapshot = ClusterMembershipSnapshot.Create(current.Value);
                    ClusterMembershipUpdate update;
                    if (ReferenceEquals(previous, default))
                    {
                        update = snapshot.CreateInitialUpdateNotification();
                    }
                    else
                    {
                        update = snapshot.CreateUpdateNotification(previous);
                    }

                    this.NotifyObservers(update);
                    previous = snapshot;
                }
            }
            catch (Exception exception)
            {
                this.log.LogError("Error processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping membership update processor");
            }
        }

        private void NotifyObservers(ClusterMembershipUpdate update)
        {
            if (!update.HasChanges) return;
            foreach (var change in update.Changes)
            {
                foreach (var listener in this.listeners)
                {
                    try
                    {
                        listener.Key.SiloStatusChangeNotification(change.SiloAddress, change.Status);
                    }
                    catch (Exception exception)
                    {
                        this.log.LogError(
                            "Exception while calling " + nameof(ISiloStatusListener.SiloStatusChangeNotification) + " on listener {Listener}: {Exception}",
                            listener,
                            exception);
                    }
                }
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>();

            lifecycle.Subscribe(nameof(SiloStatusListenerManager), ServiceLifecycleStage.RuntimeInitialize, OnStart, OnStop);

            Task OnStart(CancellationToken ct)
            {
                tasks.Add(this.ProcessMembershipUpdates());
                return Task.CompletedTask;
            }

            Task OnStop(CancellationToken ct)
            {
                this.cancellation.Cancel(throwOnFirstException: false);
                return Task.WhenAny(ct.WhenCancelled(), Task.WhenAll(tasks));
            }
        }
    }
}

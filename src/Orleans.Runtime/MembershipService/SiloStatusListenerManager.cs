using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Immutable;
using Orleans.Runtime.Utilities;

namespace Orleans.Runtime.MembershipService
{
    /// <summary>
    /// Manages <see cref="ISiloStatusListener"/> instances.
    /// </summary>
    internal class SiloStatusListenerManager : ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly object listenersLock = new object();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly MembershipTableManager membershipTableManager;
        private readonly ILogger<SiloStatusListenerManager> log;
        private readonly IFatalErrorHandler fatalErrorHandler;
        private ImmutableList<WeakReference<ISiloStatusListener>> listeners = ImmutableList<WeakReference<ISiloStatusListener>>.Empty;

        public SiloStatusListenerManager(
            MembershipTableManager membershipTableManager,
            ILogger<SiloStatusListenerManager> log,
            IFatalErrorHandler fatalErrorHandler)
        {
            this.membershipTableManager = membershipTableManager;
            this.log = log;
            this.fatalErrorHandler = fatalErrorHandler;
        }

        public bool Subscribe(ISiloStatusListener listener)
        {
            lock (this.listenersLock)
            {
                foreach (var reference in this.listeners)
                {
                    if (!reference.TryGetTarget(out var existing))
                    {
                        continue;
                    }

                    if (ReferenceEquals(existing, listener)) return false;
                }

                this.listeners = this.listeners.Add(new WeakReference<ISiloStatusListener>(listener));
                return true;
            }
        }

        public bool Unsubscribe(ISiloStatusListener listener)
        {
            lock (this.listenersLock)
            {
                for (var i = 0; i < this.listeners.Count; i++)
                {
                    if (!this.listeners[i].TryGetTarget(out var existing))
                    {
                        continue;
                    }

                    if (ReferenceEquals(existing, listener))
                    {
                        this.listeners = this.listeners.RemoveAt(i);
                        return true;
                    }
                }

                return false;
            }
        }

        private async Task ProcessMembershipUpdates()
        {
            ClusterMembershipSnapshot previous = default;
            IAsyncEnumerator<MembershipTableSnapshot> enumerator = default;
            try
            {
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Starting to process membership updates");
                enumerator = this.membershipTableManager.MembershipTableUpdates.GetAsyncEnumerator(this.cancellation.Token);
                while (await enumerator.MoveNextAsync())
                {
                    var snapshot = enumerator.Current.CreateClusterMembershipSnapshot();

                    var update = (previous is null) ? snapshot.AsUpdate() : snapshot.CreateUpdate(previous);
                    this.NotifyObservers(update);
                    previous = snapshot;
                }
            }
            catch (Exception exception) when (this.fatalErrorHandler.IsUnexpected(exception))
            {
                this.log.LogError("Error processing membership updates: {Exception}", exception);
                this.fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
            }
            finally
            {
                if (enumerator is object) await enumerator.DisposeAsync();
                if (this.log.IsEnabled(LogLevel.Debug)) this.log.LogDebug("Stopping membership update processor");
            }
        }

        private void NotifyObservers(ClusterMembershipUpdate update)
        {
            if (!update.HasChanges) return;

            List<WeakReference<ISiloStatusListener>> toRemove = null;
            var subscribers = this.listeners;
            foreach (var change in update.Changes)
            {
                for (var i = 0; i < subscribers.Count; ++i)
                {
                    if (!subscribers[i].TryGetTarget(out var listener))
                    {
                        if (toRemove is null) toRemove = new List<WeakReference<ISiloStatusListener>>();
                        toRemove.Add(subscribers[i]);
                        continue;
                    }

                    try
                    {
                        listener.SiloStatusChangeNotification(change.SiloAddress, change.Status);
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

            if (toRemove != null)
            {
                lock (this.listenersLock)
                {
                    var builder = this.listeners.ToBuilder();
                    foreach (var entry in toRemove) builder.Remove(entry);
                    this.listeners = builder.ToImmutable();
                }
            }
        }

        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            var tasks = new List<Task>();

            lifecycle.Subscribe(nameof(SiloStatusListenerManager), ServiceLifecycleStage.AfterRuntimeGrainServices, OnStart, _ => Task.CompletedTask);
            lifecycle.Subscribe(nameof(SiloStatusListenerManager), ServiceLifecycleStage.RuntimeInitialize, _ => Task.CompletedTask, OnStop);

            Task OnStart(CancellationToken ct)
            {
                tasks.Add(Task.Run(() => this.ProcessMembershipUpdates()));
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

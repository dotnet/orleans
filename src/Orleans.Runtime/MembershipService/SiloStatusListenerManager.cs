#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Immutable;
using Orleans.Internal;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Manages <see cref="ISiloStatusListener"/> instances.
/// </summary>
internal class SiloStatusListenerManager : ILifecycleParticipant<ISiloLifecycle>
{
    private readonly object _listenersLock = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly MembershipTableManager _membershipTableManager;
    private readonly ILogger<SiloStatusListenerManager> _logger;
    private readonly IFatalErrorHandler _fatalErrorHandler;
    private ImmutableList<WeakReference<ISiloStatusListener>> _listeners = [];

    public SiloStatusListenerManager(
        MembershipTableManager membershipTableManager,
        ILogger<SiloStatusListenerManager> log,
        IFatalErrorHandler fatalErrorHandler)
    {
        _membershipTableManager = membershipTableManager;
        _logger = log;
        _fatalErrorHandler = fatalErrorHandler;
    }

    public bool Subscribe(ISiloStatusListener listener)
    {
        lock (_listenersLock)
        {
            foreach (var reference in _listeners)
            {
                if (!reference.TryGetTarget(out var existing))
                {
                    continue;
                }

                if (ReferenceEquals(existing, listener)) return false;
            }

            _listeners = _listeners.Add(new WeakReference<ISiloStatusListener>(listener));
            return true;
        }
    }

    public bool Unsubscribe(ISiloStatusListener listener)
    {
        lock (_listenersLock)
        {
            for (var i = 0; i < _listeners.Count; i++)
            {
                if (!_listeners[i].TryGetTarget(out var existing))
                {
                    continue;
                }

                if (ReferenceEquals(existing, listener))
                {
                    _listeners = _listeners.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }
    }

    private async Task ProcessMembershipUpdates()
    {
        ClusterMembershipSnapshot? previous = default;
        try
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Starting to process membership updates.");
            await foreach (var tableSnapshot in _membershipTableManager.MembershipTableUpdates.WithCancellation(_cancellation.Token))
            {
                var snapshot = tableSnapshot.CreateClusterMembershipSnapshot();

                var update = (previous is null || snapshot.Version == MembershipVersion.MinValue) ? snapshot.AsUpdate() : snapshot.CreateUpdate(previous);
                NotifyObservers(update);
                previous = snapshot;
            }
        }
        catch (Exception exception) when (_fatalErrorHandler.IsUnexpected(exception))
        {
            _logger.LogError(exception, "Error processing membership updates.");
            _fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
        }
        finally
        {
            if (_logger.IsEnabled(LogLevel.Debug)) _logger.LogDebug("Stopping membership update processor.");
        }
    }

    private void NotifyObservers(ClusterMembershipUpdate update)
    {
        if (!update.HasChanges) return;

        List<WeakReference<ISiloStatusListener>>? toRemove = null;
        var subscribers = _listeners;
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
                    _logger.LogError(
                        exception,
                        "Exception while calling " + nameof(ISiloStatusListener.SiloStatusChangeNotification) + " on listener '{Listener}'.",
                        listener);
                }
            }
        }

        if (toRemove != null)
        {
            lock (_listenersLock)
            {
                var builder = _listeners.ToBuilder();
                foreach (var entry in toRemove) builder.Remove(entry);
                _listeners = builder.ToImmutable();
            }
        }
    }

    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        Task? task = null;

        lifecycle.Subscribe(nameof(SiloStatusListenerManager), ServiceLifecycleStage.AfterRuntimeGrainServices, OnStart, _ => Task.CompletedTask);
        lifecycle.Subscribe(nameof(SiloStatusListenerManager), ServiceLifecycleStage.RuntimeInitialize, _ => Task.CompletedTask, OnStop);

        Task OnStart(CancellationToken ct)
        {
            task = Task.Run(ProcessMembershipUpdates);
            return Task.CompletedTask;
        }

        async Task OnStop(CancellationToken ct)
        {
            _cancellation.Cancel(throwOnFirstException: false);
            if (task is not null)
            {
                await task.WaitAsync(ct).SuppressThrowing();
            }
        }
    }
}

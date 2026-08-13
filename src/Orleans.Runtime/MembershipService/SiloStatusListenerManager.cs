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
internal sealed partial class SiloStatusListenerManager : ILifecycleParticipant<ISiloLifecycle>
{
#if NET9_0_OR_GREATER
    private readonly Lock _listenersLock = new();
#else
    private readonly object _listenersLock = new();
#endif
    private readonly CancellationTokenSource _cancellation = new();
    private readonly IMembershipManager _membershipService;
    private readonly SiloAddress _localSiloAddress;
    private readonly ILogger<SiloStatusListenerManager> _logger;
    private readonly IFatalErrorHandler _fatalErrorHandler;
    private ImmutableList<WeakReference<ISiloStatusListener>> _listeners = [];
    private int _lastNotifiedLocalStatus;

    public SiloStatusListenerManager(
        IMembershipManager membershipManager,
        ILocalSiloDetails localSiloDetails,
        ILogger<SiloStatusListenerManager> log,
        IFatalErrorHandler fatalErrorHandler)
    {
        _membershipService = membershipManager;
        _localSiloAddress = localSiloDetails.SiloAddress;
        _logger = log;
        _fatalErrorHandler = fatalErrorHandler;
        membershipManager.LocalSiloStatusChanged += OnLocalSiloStatusChanged;
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
            LogDebugStartingToProcessMembershipUpdates();
            await foreach (var tableSnapshot in _membershipService.MembershipUpdates.WithCancellation(_cancellation.Token))
            {
                var snapshot = tableSnapshot.CreateClusterMembershipSnapshot();

                var update = (previous is null || snapshot.Version == MembershipVersion.MinValue) ? snapshot.AsUpdate() : snapshot.CreateUpdate(previous);
                NotifyObservers(update);
                previous = snapshot;
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Ignore and continue shutting down.
        }
        catch (Exception exception) when (_fatalErrorHandler.IsUnexpected(exception))
        {
            LogErrorProcessingMembershipUpdates(exception);
            _fatalErrorHandler.OnFatalException(this, nameof(ProcessMembershipUpdates), exception);
        }
        finally
        {
            LogDebugStoppingMembershipUpdateProcessor();
        }
    }

    private void NotifyObservers(ClusterMembershipUpdate update)
    {
        if (!update.HasChanges) return;

        List<WeakReference<ISiloStatusListener>>? toRemove = null;
        var subscribers = _listeners;
        foreach (var change in update.Changes)
        {
            if (change.SiloAddress.Equals(_localSiloAddress))
            {
                OnLocalSiloStatusChanged(change.Status);
                continue;
            }

            NotifyObservers(change.SiloAddress, change.Status, subscribers, ref toRemove);
        }

        RemoveDefunctListeners(toRemove);
    }

    private void OnLocalSiloStatusChanged(SiloStatus status)
    {
        while (true)
        {
            var current = Volatile.Read(ref _lastNotifiedLocalStatus);
            if (current >= (int)status)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastNotifiedLocalStatus, (int)status, current) == current)
            {
                break;
            }
        }

        List<WeakReference<ISiloStatusListener>>? toRemove = null;
        NotifyObservers(_localSiloAddress, status, _listeners, ref toRemove);
        RemoveDefunctListeners(toRemove);
    }

    private void NotifyObservers(
        SiloAddress siloAddress,
        SiloStatus status,
        ImmutableList<WeakReference<ISiloStatusListener>> subscribers,
        ref List<WeakReference<ISiloStatusListener>>? toRemove)
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
                listener.SiloStatusChangeNotification(siloAddress, status);
            }
            catch (Exception exception)
            {
                LogErrorCallingSiloStatusChangeNotification(exception, listener);
            }
        }
    }

    private void RemoveDefunctListeners(List<WeakReference<ISiloStatusListener>>? toRemove)
    {
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

        lifecycle.Subscribe(nameof(SiloStatusListenerManager), ServiceLifecycleStage.AfterRuntimeGrainServices, OnStart, NoOpStop);
        lifecycle.Subscribe(nameof(SiloStatusListenerManager), ServiceLifecycleStage.RuntimeInitialize, NoOpStart, OnStop);

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

        static Task NoOpStart(CancellationToken _) => Task.CompletedTask;
        static Task NoOpStop(CancellationToken _) => Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Starting to process membership updates."
    )]
    private partial void LogDebugStartingToProcessMembershipUpdates();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing membership updates."
    )]
    private partial void LogErrorProcessingMembershipUpdates(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Stopping membership update processor."
    )]
    private partial void LogDebugStoppingMembershipUpdateProcessor();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Exception while calling " + nameof(ISiloStatusListener.SiloStatusChangeNotification) + " on listener '{Listener}'."
    )]
    private partial void LogErrorCallingSiloStatusChangeNotification(Exception exception, ISiloStatusListener listener);
}

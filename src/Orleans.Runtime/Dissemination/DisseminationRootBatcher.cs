using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Internal;
using KeyNotification = Orleans.Runtime.Dissemination.DisseminationBroadcastQueue.KeyNotification;

namespace Orleans.Runtime.Dissemination;

// This budget counts logical root waves, not wire messages: each peer queue still owns payload
// materialization, byte limits, splitting, and RPC lifetime. Only bounded key identities live here.
internal sealed partial class DisseminationRootBatcher
{
    internal delegate bool DispatchCallback(ReadOnlySpan<KeyNotification> notifications);

    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;
    private readonly IDisseminationNamespace _namespace;
    private readonly DisseminationNamespace _namespaceName;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly DispatchCallback _dispatch;
    private readonly ILogger _logger;
    private readonly WakeTimer _timer;
    private readonly Task _worker;
    private readonly Dictionary<DisseminationKey, PendingKey> _pending = [];
    private readonly LinkedList<PendingKey> _ready = [];
    private TaskCompletionSource? _drainCompletion;
    private Exception? _failure;
    private long? _firstPendingTimestamp;
    private long? _lastDispatchTimestamp;
    private long _generation;
    private bool _dispatching;
    private bool _workerActive;
    private bool _retrying;
    private bool _forceHandoff;
    private bool _stopping;
    private bool _aborted;
    private CancellationToken _abortToken;

    public DisseminationRootBatcher(
        TimeProvider timeProvider,
        IDisseminationNamespace disseminationNamespace,
        IOptionsMonitor<DisseminationOptions> options,
        DispatchCallback dispatch,
        ILogger logger)
    {
        _timeProvider = timeProvider;
        _namespace = disseminationNamespace;
        _namespaceName = disseminationNamespace.Name;
        _options = options;
        _dispatch = dispatch;
        _logger = logger;
        _timer = new(timeProvider);
        using var _ = new ExecutionContextSuppressor();
        _worker = RunAsync();
    }

    // False means at least one distinct key was not admitted. Updates to retained keys still succeed
    // at the limit, including keys currently inside the synchronous dispatch callback.
    public bool Notify(ReadOnlySpan<KeyNotification> notifications)
    {
        var settings = GetSettings();
        var accepted = true;
        var rejected = 0;
        var wake = false;
        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            if (_stopping)
            {
                return false;
            }

            if (!settings.Enabled)
            {
                // Disabling explicitly abandons queued hints; re-enabling does not replay them.
                // Namespace state and anti-entropy, rather than this queue, own durable convergence.
                ClearPendingUnsafe();
                accepted = false;
                wake = true;
            }
            else
            {
                var wasEmpty = _pending.Count == 0;
                var now = _timeProvider.GetTimestamp();
                foreach (var notification in notifications)
                {
                    if (_pending.TryGetValue(notification.Key, out var pending))
                    {
                        // Even an unchanged notification can race an inventory/version read. Protect
                        // it from pruning without making duplicate in-flight values dirty again.
                        pending.AdmissionGeneration = ++_generation;
                        if (!notification.Force && notification.Version <= pending.Notification.Version)
                        {
                            continue;
                        }

                        pending.Notification = new(
                            notification.Key,
                            Math.Max(notification.Version, pending.Notification.Version),
                            pending.Notification.Force || notification.Force);
                        pending.Generation = pending.AdmissionGeneration;
                        pending.PendingSince ??= now;
                    }
                    else
                    {
                        if (_pending.Count >= settings.MaxPendingItemCount)
                        {
                            accepted = false;
                            rejected++;
                            continue;
                        }

                        pending = new(notification, ++_generation, now);
                        _pending.Add(notification.Key, pending);
                        _ready.AddLast(pending.Node);
                    }

                    _firstPendingTimestamp ??= now;
                    // Replacements cannot advance the earliest pending timestamp or its deadline.
                    // Avoid waking/rearming once per key while a root is collecting thousands of keys.
                    wake |= wasEmpty || settings.HighPriority;
                }
            }
        }

        if (wake)
        {
            Wake();
        }

        if (rejected > 0)
        {
            try
            {
                LogAdmissionRejected(_logger, _namespaceName, rejected, settings.MaxPendingItemCount);
            }
            catch (Exception exception)
            {
                Fail(exception);
                DisposeTimer();
            }

            for (var index = 0; index < rejected; index++)
            {
                try
                {
                    DisseminationInstruments.OnQueueAdmissionRejected(_namespaceName);
                }
                catch (Exception exception)
                {
                    try
                    {
                        LogDiagnosticFailed(_logger, exception, _namespaceName);
                    }
                    catch (Exception loggingFailure)
                    {
                        Fail(new AggregateException(exception, loggingFailure));
                        DisposeTimer();
                    }
                }
            }
        }

        return accepted;
    }

    // Flush forces all currently retained work into peer queues, not onto the wire. Concurrent
    // admissions join this drain. A partial admission never turns a forced drain into a busy loop.
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task completion;
        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            ThrowIfAbortedUnsafe();
            if (_pending.Count == 0 && !_dispatching)
            {
                return;
            }

            _drainCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _drainCompletion.Task;
        }

        Wake();
        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            ThrowIfAbortedUnsafe();
        }
    }

    // Cancellation ends this owner's drain budget and abandons remaining hints. A wave already
    // claimed for synchronous dispatch cannot be interrupted; subsequent waves will not be claimed.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _stopping = true;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Wake();
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_lock)
            {
                _aborted = true;
                _abortToken = cancellationToken;
                ClearPendingUnsafe();
            }

            DisposeTimer();
            throw;
        }

        lock (_lock)
        {
            ThrowIfFailedUnsafe();
            ThrowIfAbortedUnsafe();
        }
    }

    public void Prune(IReadOnlySet<DisseminationKey> activeKeys)
    {
        List<(DisseminationKey Key, PendingKey Pending, long Generation)> candidates = [];
        lock (_lock)
        {
            foreach (var (key, pending) in _pending)
            {
                if (!activeKeys.Contains(key))
                {
                    candidates.Add((key, pending, pending.AdmissionGeneration));
                }
            }
        }

        var retiredCount = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            // Owner inventory was captured before entering this method. A positive current version
            // means the key has since become live; namespace calls must not hold the batcher lock.
            if (_namespace.GetVersion(candidate.Key) <= 0)
            {
                candidates[retiredCount++] = candidate;
            }
        }

        lock (_lock)
        {
            for (var i = 0; i < retiredCount; i++)
            {
                var candidate = candidates[i];
                if (_pending.TryGetValue(candidate.Key, out var pending)
                    && ReferenceEquals(pending, candidate.Pending)
                    && pending.AdmissionGeneration == candidate.Generation)
                {
                    _pending.Remove(candidate.Key);
                    if (pending.Node.List is not null)
                    {
                        _ready.Remove(pending.Node);
                    }
                }
            }

            RecomputeFirstPendingUnsafe();
        }

        Wake();
    }

    // Called when the local silo loses the root role, not for ordinary membership changes. Request
    // Flush-equivalent scheduling without a drain observer; the dispatcher resolves the new root.
    // This does not store credits for later work or bypass partial-retry pacing.
    public void WakeForMembershipChange()
    {
        lock (_lock)
        {
            _forceHandoff |= _pending.Count > 0;
        }

        Wake();
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _timer.WaitAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (true)
                {
                    var settings = GetSettings();
                    lock (_lock)
                    {
                        _workerActive = true;
                        if (_failure is not null || _aborted)
                        {
                            return;
                        }

                        if (!settings.Enabled)
                        {
                            ClearPendingUnsafe();
                        }

                        if (_pending.Count == 0)
                        {
                            CompleteDrainUnsafe();
                            if (_stopping)
                            {
                                return;
                            }

                            _workerActive = false;
                            break;
                        }

                        var delay = GetDelayUnsafe(settings);
                        if (delay > TimeSpan.Zero)
                        {
                            _timer.Change(delay);
                            _workerActive = false;
                            break;
                        }
                    }

                    Dispatch();
                }
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            DisposeTimer();
            lock (_lock)
            {
                ClearPendingUnsafe();
                CompleteDrainUnsafe();
            }
        }
    }

    private TimeSpan GetDelayUnsafe(in BatchingSettings settings)
    {
        var pacedDelay = _lastDispatchTimestamp is { } last
            ? settings.BroadcastInterval - _timeProvider.GetElapsedTime(last)
            : TimeSpan.Zero;
        if (_stopping || _drainCompletion is not null || _forceHandoff || settings.HighPriority)
        {
            return _retrying ? pacedDelay : TimeSpan.Zero;
        }

        var collectionDelay = settings.CollectionDelay
            - _timeProvider.GetElapsedTime(_firstPendingTimestamp!.Value);
        return collectionDelay > pacedDelay ? collectionDelay : pacedDelay;
    }

    // Keeping snapshots in a synchronous method prevents the worker state machine from retaining
    // dispatched arrays across timer waits. Newer notifications never mutate a captured snapshot.
    private void Dispatch()
    {
        var settings = GetSettings();
        PendingDispatch[] work;
        KeyNotification[] notifications;
        lock (_lock)
        {
            if (_failure is not null || _aborted || _ready.Count == 0
                || !settings.Enabled || GetDelayUnsafe(settings) > TimeSpan.Zero)
            {
                return;
            }

            var count = Math.Min(_ready.Count, Math.Max(1, settings.MaxBatchItems));
            work = new PendingDispatch[count];
            notifications = new KeyNotification[count];
            for (var i = 0; i < count; i++)
            {
                var pending = _ready.First!.Value;
                _ready.RemoveFirst();
                notifications[i] = pending.Notification;
                work[i] = new(pending, pending.Generation, pending.PendingSince!.Value, pending.Notification);
                // A successful attempt consumes Force; a failed admission must preserve it for peers
                // which still hold an earlier same-version state.
                pending.Notification = pending.Notification with { Force = false };
                pending.PendingSince = null;
            }

            _dispatching = true;
            _lastDispatchTimestamp = _timeProvider.GetTimestamp();
        }

        var accepted = false;
        Exception? dispatchFailure = null;
        try
        {
            accepted = _dispatch(notifications);
        }
        catch (Exception exception)
        {
            dispatchFailure = exception;
        }
        finally
        {
            lock (_lock)
            {
                foreach (var item in work)
                {
                    var pending = item.Pending;
                    if (!_pending.TryGetValue(pending.Notification.Key, out var current)
                        || !ReferenceEquals(current, pending))
                    {
                        // Pruning can retire an in-flight identity and admit a new one with the same key.
                        continue;
                    }

                    if (accepted && pending.Generation == item.Generation)
                    {
                        _pending.Remove(pending.Notification.Key);
                    }
                    else
                    {
                        if (!accepted)
                        {
                            pending.PendingSince = item.PendingSince;
                            if (pending.Notification.Version == item.Notification.Version)
                            {
                                pending.Notification = pending.Notification with
                                {
                                    Force = pending.Notification.Force || item.Notification.Force,
                                };
                            }
                        }

                        _ready.AddLast(pending.Node);
                    }
                }

                _retrying = !accepted;
                _dispatching = false;
                RecomputeFirstPendingUnsafe();
                if (_pending.Count == 0)
                {
                    CompleteDrainUnsafe();
                }
            }
        }

        if (dispatchFailure is not null)
        {
            try
            {
                LogDispatchFailed(_logger, dispatchFailure, _namespaceName);
            }
            catch (Exception loggingFailure)
            {
                Fail(new AggregateException("The root dispatch recovery diagnostic failed.", dispatchFailure, loggingFailure));
            }
        }
    }

    private BatchingSettings GetSettings()
    {
        var options = _options.CurrentValue;
        var namespaceOptions = _namespace.Options;
        return new(
            options.Enabled && namespaceOptions.Enabled,
            namespaceOptions.Priority == DisseminationPriority.High,
            namespaceOptions.MaxPendingItemCount,
            options.MaxBatchItems,
            namespaceOptions.MaxCoalescingDelay,
            TimeSpan.FromSeconds(1d / Math.Clamp(options.Overlay.AggregationBroadcastsPerSecond, 1, 1000)));
    }

    private void RecomputeFirstPendingUnsafe()
    {
        _firstPendingTimestamp = null;
        foreach (var pending in _ready)
        {
            var timestamp = pending.PendingSince!.Value;
            if (_firstPendingTimestamp is null || timestamp < _firstPendingTimestamp.Value)
            {
                _firstPendingTimestamp = timestamp;
            }
        }
    }

    private void ClearPendingUnsafe()
    {
        _pending.Clear();
        _ready.Clear();
        _firstPendingTimestamp = null;
        _retrying = false;
        _forceHandoff = false;
    }

    private void CompleteDrainUnsafe()
    {
        _retrying = false;
        _forceHandoff = false;
        _drainCompletion?.TrySetResult();
        _drainCompletion = null;
    }

    private void ThrowIfFailedUnsafe()
    {
        if (_failure is { } failure)
        {
            throw new InvalidOperationException($"The dissemination root batcher for {_namespaceName} has failed.", failure);
        }
    }

    private void ThrowIfAbortedUnsafe()
    {
        if (_aborted)
        {
            throw new OperationCanceledException("The dissemination root drain was canceled.", _abortToken);
        }
    }

    private void Wake()
    {
        lock (_lock)
        {
            if (_workerActive || _failure is not null || _aborted)
            {
                return;
            }

            _workerActive = true;
        }

        try
        {
            _timer.Wake();
        }
        catch (Exception exception)
        {
            Fail(exception);
            DisposeTimer();
        }
    }

    private void DisposeTimer()
    {
        try
        {
            _timer.Dispose();
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void Fail(Exception exception)
    {
        lock (_lock)
        {
            if (_failure is not null)
            {
                return;
            }

            _failure = exception;
            // Complete normally and read _failure after awaiting: no abandoned shared task faults.
            _drainCompletion?.TrySetResult();
        }

        try
        {
            LogWorkerFailed(_logger, exception, _namespaceName);
        }
        catch
        {
            // A failing logger must not hide the original failure from admission/drain callers.
        }
    }

    private sealed class PendingKey
    {
        public KeyNotification Notification;
        public long Generation;
        public long AdmissionGeneration;
        public long? PendingSince;
        public LinkedListNode<PendingKey> Node { get; }

        public PendingKey(KeyNotification notification, long generation, long pendingSince)
        {
            Notification = notification;
            Generation = generation;
            AdmissionGeneration = generation;
            PendingSince = pendingSince;
            Node = new(this);
        }
    }

    private readonly record struct PendingDispatch(
        PendingKey Pending, long Generation, long PendingSince, KeyNotification Notification);

    private readonly record struct BatchingSettings(
        bool Enabled,
        bool HighPriority,
        int MaxPendingItemCount,
        int MaxBatchItems,
        TimeSpan CollectionDelay,
        TimeSpan BroadcastInterval);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dissemination root dispatch for {Namespace} failed and will retry at the root pacing cadence.")]
    private static partial void LogDispatchFailed(ILogger logger, Exception exception, DisseminationNamespace @namespace);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dissemination root for {Namespace} rejected {Count} new keys at its pending-key limit of {Limit}.")]
    private static partial void LogAdmissionRejected(ILogger logger, DisseminationNamespace @namespace, int count, int limit);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Dissemination root diagnostic for {Namespace} failed.")]
    private static partial void LogDiagnosticFailed(ILogger logger, Exception exception, DisseminationNamespace @namespace);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dissemination root batcher for {Namespace} failed. Admission and drain callers will observe this failure.")]
    private static partial void LogWorkerFailed(ILogger logger, Exception exception, DisseminationNamespace @namespace);
}

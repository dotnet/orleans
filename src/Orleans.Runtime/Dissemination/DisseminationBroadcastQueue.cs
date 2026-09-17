using System.Collections.Frozen;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Dissemination;

// The queue remembers what each peer has acknowledged.
// Payloads are materialized from namespace state only when a peer is ready to send.
internal sealed partial class DisseminationBroadcastQueue
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxTransportLifetime = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private readonly TimeProvider _timeProvider;
    private readonly SiloAddress _localSilo;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly FrozenDictionary<DisseminationNamespace, IDisseminationNamespace> _namespaces;
    private readonly ILogger<DisseminationBroadcastQueue> _logger;
    private readonly Action<SiloAddress, DisseminationBroadcastResponse>? _responseObserver;
    private readonly object _lock = new();
    private readonly Dictionary<SiloAddress, PeerQueuePump> _peers = [];
    private readonly DisseminationSendGate _sendGate;
    private readonly ObjectPool<Dictionary<DisseminationNamespace, HashSet<DisseminationKey>>> _inventoryPool;
    private bool _stopped;

    public DisseminationBroadcastQueue(
        TimeProvider timeProvider,
        SiloAddress localSilo,
        IInternalGrainFactory grainFactory,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationNamespace> disseminationNamespaces,
        ILogger<DisseminationBroadcastQueue> logger,
        Action<SiloAddress, DisseminationBroadcastResponse>? responseObserver = null)
    {
        _timeProvider = timeProvider;
        _localSilo = localSilo;
        _grainFactory = grainFactory;
        _options = options;
        _namespaces = disseminationNamespaces.ToFrozenDictionary(static ns => ns.Name);
        _logger = logger;
        _responseObserver = responseObserver;
        _sendGate = new(Math.Max(1, options.CurrentValue.MaxConcurrentSends));
        _inventoryPool = new DefaultObjectPool<Dictionary<DisseminationNamespace, HashSet<DisseminationKey>>>(
            new InventoryPoolPolicy(_namespaces), maximumRetained: 1);
    }

    public bool Notify(
        SiloAddress peer,
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key,
        bool force = true)
    {
        PeerQueuePump pump;
        lock (_lock)
        {
            if (_stopped)
            {
                return false;
            }

            pump = GetOrCreatePeerUnsafe(peer);
        }

        // Notification diagnostics run outside the queue lock and can reenter the queue.
        var version = disseminationNamespace.GetVersion(key);
        return pump.Notify(disseminationNamespace, [new(key, version, force)]);
    }

    public bool NotifyBatch(
        SiloAddress peer,
        IDisseminationNamespace disseminationNamespace,
        ReadOnlySpan<KeyNotification> notifications)
    {
        PeerQueuePump pump;
        lock (_lock)
        {
            if (_stopped)
            {
                return false;
            }

            if (notifications.IsEmpty)
            {
                return true;
            }

            pump = GetOrCreatePeerUnsafe(peer);
        }

        return pump.Notify(disseminationNamespace, notifications);
    }

    internal readonly record struct KeyNotification(DisseminationKey Key, long Version, bool Force);

    public void ObservePeerVersion(
        SiloAddress peer,
        DisseminationNamespace namespaceName,
        DisseminationKey key,
        long version)
    {
        if (version < 0
            || !_namespaces.TryGetValue(namespaceName, out var disseminationNamespace)
            || !disseminationNamespace.Options.Enabled)
        {
            return;
        }

        // Aggregation acknowledgments also confirm distribution processing. A peer's possession alone
        // cannot suppress distribution: its descendants may still need the value.
        if (disseminationNamespace.RoutingMode == DisseminationRoutingMode.AggregationTree)
        {
            return;
        }

        // Passive evidence only sharpens an existing ledger; it must not allocate a timer for a peer with no outbound work.
        lock (_lock)
        {
            if (_stopped || !_peers.TryGetValue(peer, out var pending))
            {
                return;
            }

            pending.ObservePeerVersion(disseminationNamespace, key, version);
        }
    }

    public async Task FlushPendingBroadcast(CancellationToken cancellationToken)
    {
        List<PeerQueuePump> peers;
        lock (_lock)
        {
            peers = [.. _peers.Values.OrderBy(static peer => peer.Peer)];
        }

        await Task.WhenAll(peers.Select(peer => peer.FlushAsync(cancellationToken).AsTask()));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<PeerQueuePump> peers;
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            peers = [.. _peers.Values];
            _peers.Clear();
        }

        try
        {
            await Task.WhenAll(peers.Select(peer => peer.StopAsync(drain: true, cancellationToken).AsTask()));
        }
        finally
        {
            _sendGate.Stop();
        }
    }

    public async Task Prune(
        DisseminationMembershipSnapshots membershipSnapshots,
        CancellationToken cancellationToken)
    {
        // Namespace identities are the authoritative inventory for retiring clean ledger entries.
        var activeKeys = _inventoryPool.Get();
        try
        {
            foreach (var disseminationNamespace in _namespaces.Values)
            {
                if (disseminationNamespace.Options.Enabled)
                {
                    activeKeys[disseminationNamespace.Name].UnionWith(disseminationNamespace.Keys);
                }
            }

            List<PeerQueuePump>? removedPeers = null;
            List<PeerQueuePump> retainedPeers;
            lock (_lock)
            {
                retainedPeers = new(_peers.Count);
                foreach (var (peer, pending) in _peers)
                {
                    if (!_localSilo.Equals(peer) && !membershipSnapshots.AllMembers.ContainsMember(peer))
                    {
                        (removedPeers ??= []).Add(pending);
                    }
                    else
                    {
                        retainedPeers.Add(pending);
                    }
                }

                if (removedPeers is not null)
                {
                    foreach (var pending in removedPeers)
                    {
                        _peers.Remove(pending.Peer);
                    }
                }
            }

            foreach (var peer in retainedPeers)
            {
                peer.PruneKeys(activeKeys, membershipSnapshots);
            }

            if (removedPeers is not null)
            {
                await Task.WhenAll(removedPeers.Select(peer => peer.StopAsync(drain: false, cancellationToken).AsTask()));
            }
        }
        finally
        {
            _inventoryPool.Return(activeKeys);
        }
    }

    private sealed class InventoryPoolPolicy(FrozenDictionary<DisseminationNamespace, IDisseminationNamespace> namespaces)
        : PooledObjectPolicy<Dictionary<DisseminationNamespace, HashSet<DisseminationKey>>>
    {
        public override Dictionary<DisseminationNamespace, HashSet<DisseminationKey>> Create()
        {
            var result = new Dictionary<DisseminationNamespace, HashSet<DisseminationKey>>(namespaces.Count);
            foreach (var ns in namespaces.Values)
            {
                result.Add(ns.Name, []);
            }

            return result;
        }

        public override bool Return(Dictionary<DisseminationNamespace, HashSet<DisseminationKey>> inventory)
        {
            foreach (var keys in inventory.Values)
            {
                keys.Clear();
            }

            return true;
        }
    }

    private PeerQueuePump GetOrCreatePeerUnsafe(SiloAddress peer)
    {
        if (!_peers.TryGetValue(peer, out var result))
        {
            result = new(peer, this);
            _peers.Add(peer, result);
        }

        return result;
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        // Failed local attempts back off independently of the root's load collection period.
        var floor = InitialRetryDelay;
        var cap = _options.CurrentValue.Overlay.AntiEntropyInterval;
        if (floor > cap)
        {
            floor = cap;
        }

        var multiplier = Math.Pow(2, Math.Min(Math.Max(0, attempt - 1), 20));
        return TimeSpan.FromTicks((long)Math.Min(cap.Ticks, floor.Ticks * multiplier));
    }

    private sealed class PeerQueuePump
    {
        private readonly DisseminationBroadcastQueue _owner;
        private readonly Action _admissionQueued;
        private readonly object _lock = new();
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly WakeTimer _flushTimer;
        private readonly Task _flushTask;
        private readonly Dictionary<DisseminationNamespace, PeerNamespaceState> _statesByNamespace = [];
        private TaskCompletionSource _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _activeFlushCompletion;
        private Exception? _pumpFailure;
        private IDisseminationSystemTarget? _target;
        // Notifications arriving during a send advance the epoch and remain dirty for the next pass.
        private long _notificationEpoch;
        private int _retryAttempt;
        private bool _stopping;
        private bool _draining;

        public PeerQueuePump(SiloAddress peer, DisseminationBroadcastQueue owner)
        {
            Peer = peer;
            _owner = owner;
            _admissionQueued = EmitAdmissionQueued;
            _flushTimer = new(owner._timeProvider);
            using var _ = new ExecutionContextSuppressor();
            _flushTask = RunScheduledFlush(_shutdownCts.Token);
        }

        public SiloAddress Peer { get; }

        private int DirtyCount { get; set; }

        public bool Notify(
            IDisseminationNamespace disseminationNamespace,
            ReadOnlySpan<KeyNotification> notifications)
        {
            ScheduledFlush? scheduled = null;
            var rejections = 0;
            var accepted = true;
            lock (_lock)
            {
                var anyChanged = false;
                foreach (var notification in notifications)
                {
                    accepted &= NotifyKeyUnsafe(
                        disseminationNamespace, notification.Key, notification.Version, notification.Force,
                        out var changed, out var rejected);
                    anyChanged |= changed;
                    rejections += rejected ? 1 : 0;
                }

                if (anyChanged)
                {
                    // Only the aggregation root collects load; peer pumps send each accepted batch immediately.
                    _flushTimer.Wake();
                    scheduled = new(DisseminationBroadcastScheduleReason.Immediate, TimeSpan.Zero, _retryAttempt, _notificationEpoch);
                }
            }

            EmitNotification(disseminationNamespace, rejections, scheduled);
            return accepted;
        }

        private bool NotifyKeyUnsafe(
            IDisseminationNamespace disseminationNamespace,
            DisseminationKey key,
            long version,
            bool force,
            out bool changed,
            out bool admissionRejected)
        {
            changed = false;
            admissionRejected = false;
            if (_stopping)
            {
                return false;
            }

            if (_pumpFailure is { } pumpFailure)
            {
                throw new InvalidOperationException($"The dissemination broadcast pump for {Peer} has failed.", pumpFailure);
            }

            var namespaceState = GetOrCreateNamespaceStateUnsafe(disseminationNamespace);
            namespaceState.Keys.TryGetValue(key, out var keyState);
            if (!force)
            {
                // Duplicate deliveries seed unknown peers without perpetuating cycles between skewed views.
                if (keyState is { Dirty: true } or { InFlight: true } && keyState.NotificationVersion >= version)
                {
                    return true;
                }

                var knownVersion = keyState?.KnownVersion;
                if (knownVersion is null && namespaceState.KnownVersions.TryGetValue(key, out var recordedVersion))
                {
                    knownVersion = recordedVersion;
                }

                if (knownVersion >= version)
                {
                    return true;
                }
            }

            if (keyState is null && namespaceState.Keys.Count >= disseminationNamespace.Options.MaxPendingItemCount)
            {
                admissionRejected = true;
                return false;
            }

            keyState ??= namespaceState.AddKey(key);
            keyState.NotificationVersion = version;
            keyState.NotificationGeneration = ++_notificationEpoch;
            _retryAttempt = 0;
            MarkDirtyUnsafe(keyState);
            changed = true;
            return true;
        }

        private void EmitNotification(IDisseminationNamespace disseminationNamespace, int rejections, ScheduledFlush? scheduled)
        {
            for (var index = 0; index < rejections; index++)
            {
                try
                {
                    DisseminationInstruments.OnQueueAdmissionRejected(disseminationNamespace.Name);
                }
                catch (Exception exception)
                {
                    LogDebugBroadcastDiagnosticFailed(_owner._logger, exception, Peer);
                }

                try
                {
                    DisseminationEvents.EmitQueueAdmissionRejected(
                        _owner._localSilo,
                        Peer,
                        disseminationNamespace.Name,
                        disseminationNamespace.Options.MaxPendingItemCount);
                }
                catch (Exception exception)
                {
                    LogDebugBroadcastDiagnosticFailed(_owner._logger, exception, Peer);
                }
            }

            EmitScheduled(scheduled);
        }

        private void EmitScheduled(ScheduledFlush? scheduled)
        {
            if (scheduled is { } info)
            {
                try
                {
                    DisseminationInstruments.OnBroadcastScheduled(info.Reason);
                }
                catch (Exception exception)
                {
                    LogDebugBroadcastDiagnosticFailed(_owner._logger, exception, Peer);
                }

                try
                {
                    DisseminationEvents.EmitBroadcastScheduled(_owner._localSilo, Peer, info.Reason, info.DueTime, info.Attempt, info.Epoch);
                }
                catch (Exception exception)
                {
                    LogDebugBroadcastDiagnosticFailed(_owner._logger, exception, Peer);
                }
            }
        }

        private void EmitAdmissionQueued()
        {
            try
            {
                DisseminationEvents.EmitSendGate(_owner._localSilo, Peer, kind: "broadcast", stage: "queued");
            }
            catch (Exception exception)
            {
                LogDebugBroadcastDiagnosticFailed(_owner._logger, exception, Peer);
            }
        }

        // Captures a scheduling decision made under the lock so the diagnostic can be emitted after the lock is released.
        private readonly record struct ScheduledFlush(
            DisseminationBroadcastScheduleReason Reason,
            TimeSpan DueTime,
            int Attempt,
            long Epoch);

        public void ObservePeerVersion(
            IDisseminationNamespace disseminationNamespace,
            DisseminationKey key,
            long version)
        {
            lock (_lock)
            {
                if (_stopping)
                {
                    return;
                }

                // Peer knowledge is evidence-driven and monotonic across acknowledgments, pushes, and anti-entropy.
                var namespaceState = GetOrCreateNamespaceStateUnsafe(disseminationNamespace);
                if (namespaceState.Keys.TryGetValue(key, out var keyState))
                {
                    if (keyState.KnownVersion is not { } knownVersion || version > knownVersion)
                    {
                        keyState.KnownVersion = version;
                    }
                }
                else
                {
                    namespaceState.ObserveKnownVersion(key, version);
                }
            }
        }

        public void PruneKeys(
            Dictionary<DisseminationNamespace, HashSet<DisseminationKey>> activeKeys,
            DisseminationMembershipSnapshots membershipSnapshots)
        {
            TaskCompletionSource? droppedFlushCompletion = null;
            var droppedDirtyCount = 0;
            lock (_lock)
            {
                foreach (var (namespaceName, namespaceState) in _statesByNamespace)
                {
                    if (!membershipSnapshots.GetSnapshot(namespaceState.Namespace.MembershipScope).ContainsMember(Peer))
                    {
                        var removedDirtyCount = namespaceState.Keys.Values.Count(static key => key.Dirty);
                        droppedDirtyCount += removedDirtyCount;
                        DirtyCount -= removedDirtyCount;
                        _statesByNamespace.Remove(namespaceName);
                        continue;
                    }

                    activeKeys.TryGetValue(namespaceName, out var namespaceKeys);
                    foreach (var (key, keyState) in namespaceState.Keys)
                    {
                        if (!keyState.Dirty
                            && !keyState.InFlight
                            && (namespaceKeys is null || !namespaceKeys.Contains(key)))
                        {
                            namespaceState.Keys.Remove(key);
                        }
                    }

                    namespaceState.PruneKnownVersions(namespaceKeys);
                    if (namespaceState.Keys.Count == 0 && namespaceState.KnownVersions.Count == 0)
                    {
                        _statesByNamespace.Remove(namespaceName);
                    }
                }

                if (droppedDirtyCount > 0 && DirtyCount == 0 && !_nextFlushCompletion.Task.IsCompleted)
                {
                    droppedFlushCompletion = _nextFlushCompletion;
                    _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            droppedFlushCompletion?.TrySetResult();
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? flushCompletion;
            lock (_lock)
            {
                if (_pumpFailure is { } pumpFailure)
                {
                    throw new InvalidOperationException($"The dissemination broadcast pump for {Peer} has failed.", pumpFailure);
                }

                if (DirtyCount > 0)
                {
                    flushCompletion = _nextFlushCompletion.Task;
                    _flushTimer.Wake();
                }
                else
                {
                    flushCompletion = _activeFlushCompletion?.Task;
                }
            }

            if (flushCompletion is null)
            {
                return;
            }

            flushCompletion.Ignore();
            await flushCompletion.WaitAsync(cancellationToken);
        }

        public async ValueTask StopAsync(bool drain, CancellationToken cancellationToken)
        {
            Task? flushCompletion;
            TaskCompletionSource? droppedFlushCompletion = null;
            var alreadyStopping = false;
            lock (_lock)
            {
                if (_stopping)
                {
                    alreadyStopping = true;
                    flushCompletion = null;
                }
                else
                {
                    _stopping = true;
                    _draining = drain;
                    if (drain)
                    {
                        if (_pumpFailure is { } pumpFailure)
                        {
                            flushCompletion = Task.FromException(pumpFailure);
                        }
                        else if (DirtyCount > 0)
                        {
                            flushCompletion = _nextFlushCompletion.Task;
                        }
                        else
                        {
                            flushCompletion = _activeFlushCompletion?.Task;
                        }
                    }
                    else
                    {
                        droppedFlushCompletion = _nextFlushCompletion;
                        _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        ClearPendingUnsafe();
                        flushCompletion = null;
                    }
                }
            }

            if (alreadyStopping)
            {
                await _flushTask.WaitAsync(cancellationToken);
                return;
            }

            droppedFlushCompletion?.TrySetResult();
            try
            {
                lock (_lock)
                {
                    if (ReferenceEquals(flushCompletion, _nextFlushCompletion.Task))
                    {
                        _flushTimer.Wake();
                    }
                }

                while (flushCompletion is not null)
                {
                    flushCompletion.Ignore();
                    await flushCompletion.WaitAsync(cancellationToken);
                    lock (_lock)
                    {
                        if (_pumpFailure is { } pumpFailure)
                        {
                            throw new InvalidOperationException($"The dissemination broadcast pump for {Peer} has failed.", pumpFailure);
                        }

                        flushCompletion = DirtyCount > 0 ? _nextFlushCompletion.Task : _activeFlushCompletion?.Task;
                    }
                }
            }
            finally
            {
                lock (_lock)
                {
                    _draining = false;
                }

                await _shutdownCts.CancelAsync();
                _flushTimer.Dispose();
                try
                {
                    await _flushTask;
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                }

                _shutdownCts.Dispose();
            }
        }

        private async Task RunScheduledFlush(CancellationToken cancellationToken)
        {
            try
            {
                while (await _flushTimer.WaitAsync(cancellationToken))
                {
                    try
                    {
                        await RunScheduledFlushIteration(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        if (!TryRecoverFromUnexpectedIterationFailure(exception))
                        {
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task RunScheduledFlushIteration(CancellationToken cancellationToken)
        {
            TaskCompletionSource flushCompletion;
            List<PendingKeyWork> work;
            long notificationEpoch;
            // Move one dirty generation to in-flight atomically; a concurrent notification can mark it dirty again.
            lock (_lock)
            {
                // All wakes issued before this drain refer to the work being consumed now.
                _flushTimer.Reset();
                if (DirtyCount == 0)
                {
                    return;
                }

                flushCompletion = _nextFlushCompletion;
                _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeFlushCompletion = flushCompletion;
                notificationEpoch = _notificationEpoch;
                work = DrainDirtyUnsafe();
            }

            var result = default(SendWorkResult);
            try
            {
                result = await SendValues(work, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Restore accepted identities before invoking diagnostics, whose callbacks can throw or reenter.
                Requeue(work);
                result = new(RequiresBackoff: true, MadeProgress: false);
                LogDebugBroadcastFlushFailed(_owner._logger, exception);
                EmitPumpFailure(DisseminationPumpFailureStatus.Recovered);
            }
            finally
            {
                ScheduledFlush? scheduled = null;
                lock (_lock)
                {
                    if (result.MadeProgress)
                    {
                        _retryAttempt = 0;
                    }

                    // A newer notification already signaled the next pass.
                    if (DirtyCount > 0 && (!_stopping || _draining) && notificationEpoch == _notificationEpoch)
                    {
                        if (result.RequiresBackoff)
                        {
                            _retryAttempt++;
                            var delay = _owner.GetRetryDelay(_retryAttempt);
                            _flushTimer.Change(delay);
                            scheduled = new(DisseminationBroadcastScheduleReason.Retry, delay, _retryAttempt, _notificationEpoch);
                        }
                        else
                        {
                            _flushTimer.Wake();
                            scheduled = new(DisseminationBroadcastScheduleReason.Immediate, TimeSpan.Zero, _retryAttempt, _notificationEpoch);
                        }
                    }

                    if (ReferenceEquals(_activeFlushCompletion, flushCompletion))
                    {
                        _activeFlushCompletion = null;
                    }
                }

                flushCompletion.TrySetResult();
                EmitScheduled(scheduled);
            }
        }

        private bool TryRecoverFromUnexpectedIterationFailure(Exception exception)
        {
            try
            {
                ScheduledFlush? scheduled = null;
                TaskCompletionSource? activeFlushCompletion;
                lock (_lock)
                {
                    if (DirtyCount > 0 && (!_stopping || _draining))
                    {
                        _retryAttempt++;
                        var delay = _owner.GetRetryDelay(_retryAttempt);
                        _flushTimer.Change(delay);
                        scheduled = new(DisseminationBroadcastScheduleReason.Retry, delay, _retryAttempt, _notificationEpoch);
                    }

                    activeFlushCompletion = _activeFlushCompletion;
                    _activeFlushCompletion = null;
                }

                activeFlushCompletion?.TrySetResult();
                // Diagnostics follow state recovery, but a failing logger must still fault the pump explicitly.
                LogWarningBroadcastPumpIterationFailed(_owner._logger, exception, Peer, scheduled?.DueTime);
                EmitPumpFailure(DisseminationPumpFailureStatus.Recovered);
                EmitScheduled(scheduled);
                return true;
            }
            catch (Exception recoveryException)
            {
                var failure = new AggregateException(
                    $"The dissemination broadcast pump for {Peer} could not recover from an iteration failure.",
                    exception,
                    recoveryException);
                FailPump(failure);
                LogErrorBroadcastPumpFailed(_owner._logger, failure, Peer);
                EmitPumpFailure(DisseminationPumpFailureStatus.Permanent);
                return false;
            }
        }

        private void EmitPumpFailure(DisseminationPumpFailureStatus status)
        {
            try
            {
                DisseminationInstruments.OnPumpFailure(status);
            }
            catch (Exception exception)
            {
                LogDebugBroadcastDiagnosticFailed(_owner._logger, exception, Peer);
            }
        }

        private void FailPump(Exception exception)
        {
            TaskCompletionSource nextFlushCompletion;
            TaskCompletionSource? activeFlushCompletion;
            lock (_lock)
            {
                _pumpFailure = exception;
                nextFlushCompletion = _nextFlushCompletion;
                _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                activeFlushCompletion = _activeFlushCompletion;
                _activeFlushCompletion = null;
                ClearPendingUnsafe();
            }

            activeFlushCompletion?.TrySetException(exception);
            nextFlushCompletion.TrySetException(exception);
        }

        private async ValueTask<SendWorkResult> SendValues(
            List<PendingKeyWork> initialWork,
            CancellationToken cancellationToken)
        {
            // Every pass re-materializes repairs from the latest acknowledged version instead of retaining serialized messages.
            var pending = new Queue<PendingKeyWork>(initialWork);
            var requiresBackoff = false;
            var madeProgress = false;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Dropping identities needs no transport capacity, including while shutdown drains disabled work.
                var enabled = _owner._options.CurrentValue.Enabled;
                while (pending.TryPeek(out var nextWork))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsWorkActive(nextWork))
                    {
                        pending.Dequeue();
                    }
                    else if (!enabled || !nextWork.Namespace.Options.Enabled)
                    {
                        pending.Dequeue();
                        CompleteUnsupported(nextWork.Namespace.Name, initialWork);
                    }
                    else
                    {
                        break;
                    }
                }

                if (pending.Count == 0)
                {
                    break;
                }

                var lease = await _owner._sendGate.AcquireAsync(Peer, _admissionQueued, cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Admission precedes serialization; waiting destinations retain only identities.
                    var currentOptions = _owner._options.CurrentValue;
                    var batch = new Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>>();
                    var sentKeys = new List<SentKey>();
                    var itemCount = 0;
                    var byteCount = 0;

                    while (pending.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var work = pending.Peek();
                        if (!IsWorkActive(work))
                        {
                            pending.Dequeue();
                            continue;
                        }

                        if (!currentOptions.Enabled || !work.Namespace.Options.Enabled)
                        {
                            pending.Dequeue();
                            CompleteUnsupported(work.Namespace.Name, initialWork);
                            continue;
                        }

                        var knownVersion = GetKnownVersion(work);
                        var request = new DisseminationRepairRequest(
                            work.Key,
                            knownVersion,
                            currentOptions.MaxBatchBytes - byteCount,
                            work.Namespace.Options.MaxPayloadBytes);
                        var repair = work.Namespace.CreateRepair(request);
                        if (repair.Status is DisseminationRepairStatus.Current)
                        {
                            // The namespace confirms that the peer is already current, so no RPC is needed.
                            pending.Dequeue();
                            CompleteCurrent(work, repair.Version);
                            continue;
                        }

                        if (repair.Status is DisseminationRepairStatus.InsufficientCapacity && itemCount > 0)
                        {
                            // Give this key a fresh budget in the next batch.
                            break;
                        }

                        if (repair.Status is DisseminationRepairStatus.InsufficientCapacity)
                        {
                            // A value which cannot fit in an empty batch waits for a future publication to change it.
                            pending.Dequeue();
                            CompleteUnsendable(work);
                            continue;
                        }

                        if (repair.Status is DisseminationRepairStatus.Unavailable
                            && !IsActiveKey(work.Namespace, work.Key))
                        {
                            // A key absent from current digests was removed, not transiently unavailable.
                            pending.Dequeue();
                            CompleteRemoved(work);
                            continue;
                        }

                        if (repair.Status is not DisseminationRepairStatus.Produced
                            || !ValidateRepair(request, repair))
                        {
                            // Invalid or temporarily unavailable repairs retain the dirty key and enter backoff.
                            pending.Dequeue();
                            Requeue([work]);
                            requiresBackoff = true;
                            continue;
                        }

                        pending.Dequeue();
                        ref var namespaceValues = ref CollectionsMarshal.GetValueRefOrAddDefault(
                            batch,
                            work.Namespace.Name,
                            out _);
                        namespaceValues ??= [];
                        var value = repair.Value;
                        namespaceValues.Add(new DisseminationBroadcastValue
                        {
                            Value = value,
                            TimeToLive = work.Namespace.Options.StaleItemTtl,
                        });
                        itemCount++;
                        byteCount += value.Payload.Length;

                        sentKeys.Add(new(work, knownVersion, value.ToVersion));
                        if (itemCount >= currentOptions.MaxBatchItems || byteCount >= currentOptions.MaxBatchBytes)
                        {
                            break;
                        }
                    }

                    if (itemCount == 0)
                    {
                        continue;
                    }

                    var responseTask = SendBatch(batch, cancellationToken);
                    batch = null!;
                    var response = await responseTask;
                    if (response is null)
                    {
                        Requeue(sentKeys.Select(static sent => sent.Work));
                        Requeue(pending);
                        requiresBackoff = true;
                        break;
                    }

                    // RPC completion is not application evidence; only the returned receiver versions advance the ledger.
                    _owner._responseObserver?.Invoke(Peer, response);
                    var acknowledgments = response.AllVersionsAcknowledged ? null : CreateAcknowledgmentLookup(response.Acknowledgments);
                    var unsupportedNamespaces = response.UnsupportedNamespaces.Count > 0
                        ? response.UnsupportedNamespaces.ToHashSet()
                        : null;
                    foreach (var sent in sentKeys)
                    {
                        if (unsupportedNamespaces?.Contains(sent.Work.Namespace.Name) == true)
                        {
                            CompleteUnsupported(sent.Work.Namespace.Name, initialWork);
                            continue;
                        }

                        var acknowledgedVersion = sent.SentVersion;
                        if (acknowledgments is not null && !acknowledgments.TryGetValue(
                            new(sent.Work.Namespace.Name, sent.Work.Key),
                            out acknowledgedVersion))
                        {
                            if (!CompleteFromExistingEvidence(sent))
                            {
                                requiresBackoff = true;
                            }

                            continue;
                        }

                        var completion = CompleteAcknowledged(sent, acknowledgedVersion);
                        madeProgress |= completion.MadeProgress;
                        requiresBackoff |= completion.RequiresBackoff;
                    }
                }
                finally
                {
                    lease.Dispose();
                }
            }

            return new(requiresBackoff, madeProgress);
        }

        private async ValueTask<DisseminationBroadcastResponse?> SendBatch(
            Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> valuesByNamespace,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var transportLifetime = TimeSpan.MaxValue;
            foreach (var values in valuesByNamespace.Values)
            {
                foreach (var value in values)
                {
                    if (value.TimeToLive < transportLifetime)
                    {
                        transportLifetime = value.TimeToLive;
                    }
                }
            }
            if (transportLifetime <= TimeSpan.Zero)
            {
                return null;
            }

            if (transportLifetime > MaxTransportLifetime)
            {
                transportLifetime = MaxTransportLifetime;
            }

            using var lifetimeCancellation = new CancellationTokenSource(transportLifetime, _owner._timeProvider);
            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lifetimeCancellation.Token);
            try
            {
                var batch = new DisseminationBroadcastBatch
                {
                    Sender = _owner._localSilo,
                    Values = valuesByNamespace,
                    SupportsCompactAcknowledgments = true,
                };
                sendCancellation.Token.ThrowIfCancellationRequested();
                var sendTask = GetTarget().PushBroadcast(batch, sendCancellation.Token);
                try
                {
                    var response = await sendTask.WaitAsync(sendCancellation.Token);
                    try
                    {
                        DisseminationInstruments.OnBroadcastSent(batch.Values, "tree");
                    }
                    catch (Exception exception)
                    {
                        LogDebugBroadcastDiagnosticFailed(_owner._logger, exception, Peer);
                    }

                    return response;
                }
                catch (OperationCanceledException) when (sendCancellation.IsCancellationRequested)
                {
                    ObserveLateSend(sendTask);
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                EmitSendFailure(DisseminationFailureReason.Timeout);
                LogDebugBroadcastTransportLifetimeExpired(_owner._logger, Peer, transportLifetime);
                return null;
            }
            catch (Exception exception)
            {
                EmitSendFailure(DisseminationFailureReason.Error);
                LogDebugDisseminationSendFailed(_owner._logger, exception, Peer);
                return null;
            }
        }

        private void EmitSendFailure(DisseminationFailureReason reason)
        {
            try
            {
                DisseminationInstruments.OnBroadcastSendFailure(reason);
            }
            catch (Exception exception)
            {
                LogDebugBroadcastDiagnosticFailed(_owner._logger, exception, Peer);
            }
        }

        private void ObserveLateSend(Task<DisseminationBroadcastResponse> sendTask)
        {
            // Fault observation follows transport completion after the operation's cancellation.
            sendTask.ContinueWith(
                task => LogDebugDisseminationSendFailed(_owner._logger, task.Exception!, Peer),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default).Ignore();
        }

        private IDisseminationSystemTarget GetTarget() =>
            _target ??= _owner._grainFactory.GetSystemTarget<IDisseminationSystemTarget>(
                Constants.DisseminationSystemTargetType,
                Peer);

        private List<PendingKeyWork> DrainDirtyUnsafe()
        {
            // Snapshot dirty work into an in-flight generation; any later notification will set Dirty again.
            // Membership precedes load when a peer has both kinds of work.
            var result = new List<PendingKeyWork>(DirtyCount);
            foreach (var namespaceState in _statesByNamespace.Values
                .OrderBy(static state => state.Namespace.RoutingMode == DisseminationRoutingMode.AggregationTree))
            {
                foreach (var (key, keyState) in namespaceState.Keys)
                {
                    if (!keyState.Dirty)
                    {
                        continue;
                    }

                    keyState.Dirty = false;
                    keyState.InFlight = true;
                    DirtyCount--;
                    result.Add(new(
                        namespaceState.Namespace,
                        key,
                        keyState.NotificationGeneration,
                        keyState.KnownVersion,
                        namespaceState));
                }
            }

            return result;
        }

        private long? GetKnownVersion(PendingKeyWork work)
        {
            lock (_lock)
            {
                return TryGetKeyStateUnsafe(work, out _, out var keyState)
                    ? keyState.KnownVersion
                    : work.KnownVersion;
            }
        }

        private bool IsWorkActive(PendingKeyWork work)
        {
            lock (_lock)
            {
                return TryGetKeyStateUnsafe(work, out _, out var keyState) && keyState.InFlight;
            }
        }

        private void CompleteCurrent(PendingKeyWork work, long version)
        {
            lock (_lock)
            {
                if (!TryGetKeyStateUnsafe(work, out var namespaceState, out var keyState))
                {
                    return;
                }

                if (keyState.KnownVersion is not { } knownVersion || version > knownVersion)
                {
                    keyState.KnownVersion = version;
                }

                keyState.InFlight = false;
                if (keyState.NotificationGeneration == work.NotificationGeneration && !keyState.Dirty)
                {
                    namespaceState.RetireKey(work.Key, keyState);
                }
            }
        }

        private SendWorkResult CompleteAcknowledged(SentKey sent, long acknowledgedVersion)
        {
            lock (_lock)
            {
                if (!TryGetKeyStateUnsafe(sent.Work, out var namespaceState, out var keyState))
                {
                    return default;
                }

                var previousVersion = keyState.KnownVersion;
                if (previousVersion is null || acknowledgedVersion > previousVersion)
                {
                    keyState.KnownVersion = acknowledgedVersion;
                }

                var madeProgress = sent.FromVersion is null
                    ? acknowledgedVersion > 0
                    : acknowledgedVersion > sent.FromVersion;
                keyState.InFlight = false;
                // Retire only the generation we sent; a newer notification remains queued even if this repair reached its target.
                if (keyState.NotificationGeneration != sent.Work.NotificationGeneration
                    || keyState.Dirty)
                {
                    return new(RequiresBackoff: false, madeProgress);
                }

                if (keyState.KnownVersion >= sent.SentVersion)
                {
                    namespaceState.RetireKey(sent.Work.Key, keyState);
                    return new(RequiresBackoff: false, madeProgress);
                }

                MarkDirtyUnsafe(keyState);
                return new(RequiresBackoff: true, madeProgress);
            }
        }

        private bool CompleteFromExistingEvidence(SentKey sent)
        {
            // A concurrent digest observation can satisfy a response which omitted this key's acknowledgment.
            lock (_lock)
            {
                if (!TryGetKeyStateUnsafe(sent.Work, out var namespaceState, out var keyState))
                {
                    return true;
                }

                if (keyState.KnownVersion >= sent.SentVersion)
                {
                    keyState.InFlight = false;
                    if (keyState.NotificationGeneration == sent.Work.NotificationGeneration && !keyState.Dirty)
                    {
                        namespaceState.RetireKey(sent.Work.Key, keyState);
                    }

                    return true;
                }

                keyState.InFlight = false;
                MarkDirtyUnsafe(keyState);
                return false;
            }
        }

        private void CompleteUnsupported(DisseminationNamespace namespaceName, List<PendingKeyWork> work)
        {
            // Capability evidence completes this flush's generations, not publications made during the send.
            lock (_lock)
            {
                if (!_statesByNamespace.TryGetValue(namespaceName, out var namespaceState))
                {
                    return;
                }

                var matchesState = false;
                foreach (var item in work)
                {
                    if (item.Namespace.Name != namespaceName
                        || !ReferenceEquals(item.NamespaceState, namespaceState))
                    {
                        continue;
                    }

                    matchesState = true;
                    if (!namespaceState.Keys.TryGetValue(item.Key, out var keyState))
                    {
                        continue;
                    }

                    keyState.InFlight = false;
                    if (keyState.NotificationGeneration != item.NotificationGeneration)
                    {
                        MarkDirtyUnsafe(keyState);
                        continue;
                    }

                    if (keyState.Dirty)
                    {
                        DirtyCount--;
                    }

                    namespaceState.Keys.Remove(item.Key);
                }

                if (!matchesState)
                {
                    return;
                }

                namespaceState.KnownVersions.Clear();
                foreach (var keyState in namespaceState.Keys.Values)
                {
                    keyState.KnownVersion = null;
                }

                if (namespaceState.Keys.Count == 0)
                {
                    _statesByNamespace.Remove(namespaceName);
                }

                if (DirtyCount == 0)
                {
                    var completion = _nextFlushCompletion;
                    _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    completion.TrySetResult();
                }
            }
        }

        private void CompleteRemoved(PendingKeyWork work)
        {
            // Removed keys leave no retry state behind unless a newer notification raced with the repair.
            lock (_lock)
            {
                if (!TryGetKeyStateUnsafe(work, out var namespaceState, out var keyState))
                {
                    return;
                }

                if (keyState.NotificationGeneration != work.NotificationGeneration || keyState.Dirty)
                {
                    keyState.InFlight = false;
                    return;
                }

                namespaceState.Keys.Remove(work.Key);
                if (namespaceState.Keys.Count == 0)
                {
                    _statesByNamespace.Remove(work.Namespace.Name);
                }
            }
        }

        private void CompleteUnsendable(PendingKeyWork work)
        {
            // Release admission capacity while preserving peer knowledge for a later publication.
            lock (_lock)
            {
                if (TryGetKeyStateUnsafe(work, out var namespaceState, out var keyState))
                {
                    keyState.InFlight = false;
                    if (keyState.NotificationGeneration == work.NotificationGeneration && !keyState.Dirty)
                    {
                        namespaceState.RetireKey(work.Key, keyState);
                    }
                }
            }
        }

        private void Requeue(IEnumerable<PendingKeyWork> work)
        {
            lock (_lock)
            {
                if (_stopping && !_draining)
                {
                    foreach (var item in work)
                    {
                        if (TryGetKeyStateUnsafe(item, out _, out var keyState))
                        {
                            keyState.InFlight = false;
                        }
                    }

                    return;
                }

                foreach (var item in work)
                {
                    if (TryGetKeyStateUnsafe(item, out _, out var keyState))
                    {
                        keyState.InFlight = false;
                        MarkDirtyUnsafe(keyState);
                    }
                }
            }
        }

        private void ClearPendingUnsafe()
        {
            _statesByNamespace.Clear();
            DirtyCount = 0;
            _retryAttempt = 0;
        }

        private PeerNamespaceState GetOrCreateNamespaceStateUnsafe(IDisseminationNamespace disseminationNamespace)
        {
            if (!_statesByNamespace.TryGetValue(disseminationNamespace.Name, out var result))
            {
                result = new(disseminationNamespace);
                _statesByNamespace.Add(disseminationNamespace.Name, result);
            }

            return result;
        }

        private bool TryGetKeyStateUnsafe(
            PendingKeyWork work,
            out PeerNamespaceState namespaceState,
            out PeerKeyState keyState)
        {
            if (_statesByNamespace.TryGetValue(work.Namespace.Name, out namespaceState!)
                && ReferenceEquals(namespaceState, work.NamespaceState)
                && namespaceState.Keys.TryGetValue(work.Key, out keyState!))
            {
                return true;
            }

            namespaceState = null!;
            keyState = null!;
            return false;
        }

        private void MarkDirtyUnsafe(PeerKeyState keyState)
        {
            if (keyState.Dirty)
            {
                return;
            }

            keyState.Dirty = true;
            DirtyCount++;
        }

        private static Dictionary<DigestKey, long> CreateAcknowledgmentLookup(
            Dictionary<DisseminationNamespace, List<DigestEntry>> acknowledgments)
        {
            var result = new Dictionary<DigestKey, long>();
            foreach (var (namespaceName, entries) in acknowledgments)
            {
                foreach (var entry in entries)
                {
                    var key = new DigestKey(namespaceName, entry.Key);
                    if (!result.TryGetValue(key, out var version) || entry.Version > version)
                    {
                        result[key] = entry.Version;
                    }
                }
            }

            return result;
        }

        private static bool IsActiveKey(
            IDisseminationNamespace disseminationNamespace,
            DisseminationKey key) =>
            disseminationNamespace.Digests.Any(entry => entry.Key == key);

        private static bool ValidateRepair(
            in DisseminationRepairRequest request,
            in DisseminationRepairResult repair)
        {
            var value = repair.Value;
            return repair.Status is DisseminationRepairStatus.Produced
                && repair.Version > 0
                && value.Key == request.Key
                && value.FromVersion == 0
                && value.ToVersion == repair.Version
                && value.Payload.Length <= request.MaxPayloadBytes
                && value.Payload.Length <= request.MaxBatchBytes;
        }

        private sealed class PeerNamespaceState(IDisseminationNamespace disseminationNamespace)
        {
            public IDisseminationNamespace Namespace { get; } = disseminationNamespace;

            public Dictionary<DisseminationKey, PeerKeyState> Keys { get; } = [];

            public Dictionary<DisseminationKey, long> KnownVersions { get; } = [];

            public PeerKeyState AddKey(DisseminationKey key)
            {
                var result = new PeerKeyState();
                if (KnownVersions.TryGetValue(key, out var knownVersion))
                {
                    result.KnownVersion = knownVersion;
                }

                Keys.Add(key, result);
                return result;
            }

            public void ObserveKnownVersion(DisseminationKey key, long version)
            {
                if (!KnownVersions.TryGetValue(key, out var knownVersion) || version > knownVersion)
                {
                    KnownVersions[key] = version;
                }
            }

            public void RetireKey(DisseminationKey key, PeerKeyState keyState)
            {
                if (keyState.KnownVersion is { } knownVersion)
                {
                    ObserveKnownVersion(key, knownVersion);
                }

                Keys.Remove(key);
            }

            public void PruneKnownVersions(HashSet<DisseminationKey>? activeKeys)
            {
                foreach (var key in KnownVersions.Keys)
                {
                    if (activeKeys is null || !activeKeys.Contains(key))
                    {
                        KnownVersions.Remove(key);
                    }
                }
            }
        }

        private sealed class PeerKeyState
        {
            // A null version means no known baseline. Dirty can coexist with InFlight when a newer notification arrives mid-send.
            public long? KnownVersion { get; set; }

            public long NotificationGeneration { get; set; }

            public long NotificationVersion { get; set; }

            public bool Dirty { get; set; }

            public bool InFlight { get; set; }
        }

        private readonly record struct PendingKeyWork(
            IDisseminationNamespace Namespace,
            DisseminationKey Key,
            long NotificationGeneration,
            long? KnownVersion,
            PeerNamespaceState NamespaceState);

        private readonly record struct SentKey(
            PendingKeyWork Work,
            long? FromVersion,
            long SentVersion);

        private readonly record struct DigestKey(
            DisseminationNamespace Namespace,
            DisseminationKey Key);

        private readonly record struct SendWorkResult(bool RequiresBackoff, bool MadeProgress);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination send to {Peer} failed.")]
    private static partial void LogDebugDisseminationSendFailed(
        ILogger logger,
        Exception exception,
        SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination broadcast batch flush failed.")]
    private static partial void LogDebugBroadcastFlushFailed(
        ILogger logger,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Dissemination broadcast pump for {Peer} encountered an unexpected iteration failure and will retry after {RetryDelay}.")]
    private static partial void LogWarningBroadcastPumpIterationFailed(
        ILogger logger,
        Exception exception,
        SiloAddress peer,
        TimeSpan? retryDelay);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Dissemination broadcast pump for {Peer} failed permanently. Pending flush and drain waiters will fail explicitly.")]
    private static partial void LogErrorBroadcastPumpFailed(
        ILogger logger,
        Exception exception,
        SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination broadcast diagnostic for {Peer} failed.")]
    private static partial void LogDebugBroadcastDiagnosticFailed(
        ILogger logger,
        Exception exception,
        SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination broadcast transport to {Peer} exceeded its remaining hop lifetime of {Lifetime}.")]
    private static partial void LogDebugBroadcastTransportLifetimeExpired(
        ILogger logger,
        SiloAddress peer,
        TimeSpan lifetime);
}

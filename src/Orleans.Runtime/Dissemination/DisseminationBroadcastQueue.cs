using System.Collections.Frozen;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationBroadcastQueue
{
    private readonly TimeProvider _timeProvider;
    private readonly SiloAddress _localSilo;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly IDisseminationNamespace[] _disseminationNamespaces;
    private readonly FrozenDictionary<DisseminationNamespace, IDisseminationNamespace> _namespaces;
    private readonly ILogger<DisseminationBroadcastQueue> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<SiloAddress, PeerQueuePump> _peers = [];
    private readonly SemaphoreSlim _sendGate;
    private bool _stopped;

    public DisseminationBroadcastQueue(
        TimeProvider timeProvider,
        SiloAddress localSilo,
        IInternalGrainFactory grainFactory,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationNamespace> disseminationNamespaces,
        ILogger<DisseminationBroadcastQueue> logger)
    {
        _timeProvider = timeProvider;
        _localSilo = localSilo;
        _grainFactory = grainFactory;
        _options = options;
        _disseminationNamespaces = [.. disseminationNamespaces];
        _namespaces = _disseminationNamespaces.ToFrozenDictionary(static ns => ns.Name);
        _logger = logger;
        _sendGate = new(Math.Max(1, options.CurrentValue.MaxConcurrentSends));
    }

    public void Notify(
        SiloAddress peer,
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            GetOrCreatePeerUnsafe(peer).Notify(disseminationNamespace, key);
        }
    }

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

        await Task.WhenAll(peers.Select(peer => peer.StopAsync(drain: true, cancellationToken).AsTask()));
    }

    public async Task Prune(
        DisseminationMembershipSnapshot membershipSnapshot,
        CancellationToken cancellationToken)
    {
        var activeKeys = new Dictionary<DisseminationNamespace, HashSet<DisseminationKey>>();
        foreach (var disseminationNamespace in _disseminationNamespaces)
        {
            if (disseminationNamespace.Options.Enabled)
            {
                activeKeys[disseminationNamespace.Name] = disseminationNamespace.Digests
                    .Select(static entry => entry.Key)
                    .ToHashSet();
            }
        }

        List<PeerQueuePump>? removedPeers = null;
        List<PeerQueuePump> retainedPeers;
        lock (_lock)
        {
            retainedPeers = new(_peers.Count);
            foreach (var (peer, pending) in _peers)
            {
                if (!_localSilo.Equals(peer) && !membershipSnapshot.ContainsMember(peer))
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
            peer.PruneKeys(activeKeys);
        }

        if (removedPeers is not null)
        {
            await Task.WhenAll(removedPeers.Select(peer => peer.StopAsync(drain: false, cancellationToken).AsTask()));
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

    private TimeSpan GetCoalescingDelay(TimeSpan namespaceDelay)
    {
        var result = namespaceDelay;
        foreach (var disseminationNamespace in _disseminationNamespaces)
        {
            var namespaceOptions = disseminationNamespace.Options;
            if (namespaceOptions.Enabled && namespaceOptions.MaxCoalescingDelay < result)
            {
                result = namespaceOptions.MaxCoalescingDelay;
            }
        }

        return result;
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        var floor = TimeSpan.MaxValue;
        foreach (var disseminationNamespace in _disseminationNamespaces)
        {
            var namespaceOptions = disseminationNamespace.Options;
            if (namespaceOptions.Enabled && namespaceOptions.MaxCoalescingDelay < floor)
            {
                floor = namespaceOptions.MaxCoalescingDelay;
            }
        }

        if (floor == TimeSpan.MaxValue)
        {
            floor = TimeSpan.FromMilliseconds(100);
        }

        var cap = _options.CurrentValue.Overlay.AntiEntropyInterval;
        if (cap < floor)
        {
            cap = floor;
        }

        var multiplier = Math.Pow(2, Math.Min(Math.Max(0, attempt - 1), 20));
        return TimeSpan.FromTicks((long)Math.Min(cap.Ticks, floor.Ticks * multiplier));
    }

    private sealed class PeerQueuePump
    {
        private readonly DisseminationBroadcastQueue _owner;
        private readonly object _lock = new();
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly WakeTimer _flushTimer;
        private readonly Task _flushTask;
        private Dictionary<DisseminationNamespace, PeerNamespaceState> _statesByNamespace = [];
        private TaskCompletionSource _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _activeFlushCompletion;
        private IDisseminationSystemTarget? _target;
        private long _notificationEpoch;
        private int _retryAttempt;
        private bool _wakeScheduled;
        private bool _stopping;

        public PeerQueuePump(SiloAddress peer, DisseminationBroadcastQueue owner)
        {
            Peer = peer;
            _owner = owner;
            _flushTimer = new(owner._timeProvider);
            using var _ = new ExecutionContextSuppressor();
            _flushTask = RunScheduledFlush();
        }

        public SiloAddress Peer { get; }

        private int DirtyCount { get; set; }

        public void Notify(IDisseminationNamespace disseminationNamespace, DisseminationKey key)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                var namespaceState = GetOrCreateNamespaceStateUnsafe(disseminationNamespace);
                var keyState = namespaceState.GetOrCreateKey(key);
                keyState.NotificationGeneration++;
                _notificationEpoch++;
                var wasRetrying = _retryAttempt > 0;
                _retryAttempt = 0;
                var wasEmpty = DirtyCount == 0;
                MarkDirtyUnsafe(namespaceState, keyState);

                var currentOptions = _owner._options.CurrentValue;
                if (DirtyCount >= currentOptions.MaxBatchItems
                    || namespaceState.DirtyCount >= disseminationNamespace.Options.MaxPendingItemCount)
                {
                    _flushTimer.Change(TimeSpan.Zero);
                    _wakeScheduled = true;
                }
                else if (wasEmpty || wasRetrying || !_wakeScheduled)
                {
                    _flushTimer.Change(_owner.GetCoalescingDelay(disseminationNamespace.Options.MaxCoalescingDelay));
                    _wakeScheduled = true;
                }
            }
        }

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

                var keyState = GetOrCreateNamespaceStateUnsafe(disseminationNamespace).GetOrCreateKey(key);
                if (keyState.KnownVersion is not { } knownVersion || version > knownVersion)
                {
                    keyState.KnownVersion = version;
                }
            }
        }

        public void PruneKeys(Dictionary<DisseminationNamespace, HashSet<DisseminationKey>> activeKeys)
        {
            lock (_lock)
            {
                foreach (var (namespaceName, namespaceState) in _statesByNamespace.ToArray())
                {
                    activeKeys.TryGetValue(namespaceName, out var namespaceKeys);
                    foreach (var (key, keyState) in namespaceState.Keys.ToArray())
                    {
                        if (!keyState.Dirty
                            && !keyState.InFlight
                            && (namespaceKeys is null || !namespaceKeys.Contains(key)))
                        {
                            namespaceState.Keys.Remove(key);
                        }
                    }

                    if (namespaceState.Keys.Count == 0)
                    {
                        _statesByNamespace.Remove(namespaceName);
                    }
                }
            }
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? flushCompletion;
            var wake = false;
            lock (_lock)
            {
                if (DirtyCount > 0)
                {
                    flushCompletion = _nextFlushCompletion.Task;
                    wake = true;
                    _wakeScheduled = true;
                }
                else
                {
                    flushCompletion = _activeFlushCompletion;
                }
            }

            if (flushCompletion is null)
            {
                return;
            }

            if (wake)
            {
                _flushTimer.Wake();
            }

            await flushCompletion.WaitAsync(cancellationToken);
        }

        public async ValueTask StopAsync(bool drain, CancellationToken cancellationToken)
        {
            Task? flushCompletion;
            TaskCompletionSource? droppedFlushCompletion = null;
            var wake = false;
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
                    if (drain)
                    {
                        if (DirtyCount > 0)
                        {
                            flushCompletion = _nextFlushCompletion.Task;
                            wake = true;
                            _wakeScheduled = true;
                        }
                        else
                        {
                            flushCompletion = _activeFlushCompletion;
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
                if (wake)
                {
                    _flushTimer.Wake();
                }

                if (flushCompletion is not null)
                {
                    await flushCompletion.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                if (!drain || cancellationToken.IsCancellationRequested)
                {
                    await _shutdownCts.CancelAsync();
                }

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

        private async Task RunScheduledFlush()
        {
            try
            {
                var cancellationToken = _shutdownCts.Token;
                while (await _flushTimer.WaitAsync(cancellationToken))
                {
                    TaskCompletionSource flushCompletion;
                    List<PendingKeyWork> work;
                    long notificationEpoch;
                    lock (_lock)
                    {
                        _wakeScheduled = false;
                        if (DirtyCount == 0)
                        {
                            continue;
                        }

                        flushCompletion = _nextFlushCompletion;
                        _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _activeFlushCompletion = flushCompletion.Task;
                        notificationEpoch = _notificationEpoch;
                        work = DrainDirtyUnsafe();
                    }

                    var result = SendWorkResult.None;
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
                        LogDebugBroadcastFlushFailed(_owner._logger, exception);
                        Requeue(work);
                        result = new(RequiresBackoff: true, MadeProgress: false);
                    }
                    finally
                    {
                        flushCompletion.TrySetResult();
                        lock (_lock)
                        {
                            if (ReferenceEquals(_activeFlushCompletion, flushCompletion.Task))
                            {
                                _activeFlushCompletion = null;
                            }

                            if (result.MadeProgress)
                            {
                                _retryAttempt = 0;
                            }

                            if (DirtyCount > 0 && !_stopping)
                            {
                                if (notificationEpoch == _notificationEpoch)
                                {
                                    if (result.RequiresBackoff)
                                    {
                                        _retryAttempt++;
                                        _flushTimer.Change(_owner.GetRetryDelay(_retryAttempt));
                                    }
                                    else
                                    {
                                        _flushTimer.Change(_owner.GetCoalescingDelay(TimeSpan.MaxValue));
                                    }

                                    _wakeScheduled = true;
                                }
                                else if (!_wakeScheduled)
                                {
                                    _flushTimer.Change(_owner.GetCoalescingDelay(TimeSpan.MaxValue));
                                    _wakeScheduled = true;
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogDebugBroadcastFlushFailed(_owner._logger, exception);
            }
        }

        private async ValueTask<SendWorkResult> SendValues(
            List<PendingKeyWork> initialWork,
            CancellationToken cancellationToken)
        {
            var pending = new Queue<PendingKeyWork>(initialWork);
            var requiresBackoff = false;
            var madeProgress = false;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentOptions = _owner._options.CurrentValue;
                var batch = new Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>>();
                var sentKeys = new List<SentKey>();
                var itemCount = 0;
                var byteCount = 0;

                while (pending.Count > 0)
                {
                    var work = pending.Peek();
                    if (!IsWorkActive(work))
                    {
                        pending.Dequeue();
                        continue;
                    }

                    if (!work.Namespace.Options.Enabled)
                    {
                        pending.Dequeue();
                        CompleteUnsupported(work);
                        continue;
                    }

                    var knownVersion = GetKnownVersion(work);
                    var request = new DisseminationRepairRequest(
                        work.Key,
                        knownVersion,
                        toVersion: null,
                        currentOptions.MaxBatchItems - itemCount,
                        currentOptions.MaxBatchBytes - byteCount,
                        work.Namespace.Options.MaxPayloadBytes);
                    var repair = work.Namespace.CreateRepair(request);
                    if (repair.Status is DisseminationRepairStatus.Current)
                    {
                        pending.Dequeue();
                        CompleteCurrent(work, repair.Version);
                        continue;
                    }

                    if (repair.Status is DisseminationRepairStatus.InsufficientCapacity && itemCount > 0)
                    {
                        break;
                    }

                    if (repair.Status is DisseminationRepairStatus.InsufficientCapacity)
                    {
                        pending.Dequeue();
                        CompleteUnsendable(work);
                        continue;
                    }

                    if (repair.Status is DisseminationRepairStatus.Unavailable
                        && !IsActiveKey(work.Namespace, work.Key))
                    {
                        pending.Dequeue();
                        CompleteRemoved(work);
                        continue;
                    }

                    if (repair.Status is not DisseminationRepairStatus.Produced
                        || !ValidateRepair(request, repair))
                    {
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
                    foreach (var value in repair.Values)
                    {
                        namespaceValues.Add(new DisseminationBroadcastValue
                        {
                            Value = value,
                            ExpiresAt = _owner._timeProvider.GetUtcNow() + work.Namespace.Options.StaleItemTtl,
                        });
                        itemCount++;
                        byteCount += value.Payload.Length;
                    }

                    sentKeys.Add(new(work, knownVersion, repair.Version));
                    if (itemCount >= currentOptions.MaxBatchItems || byteCount >= currentOptions.MaxBatchBytes)
                    {
                        break;
                    }
                }

                if (itemCount == 0)
                {
                    continue;
                }

                var response = await SendBatch(batch, cancellationToken);
                if (response is null)
                {
                    Requeue(sentKeys.Select(static sent => sent.Work));
                    Requeue(pending);
                    requiresBackoff = true;
                    break;
                }

                var acknowledgments = CreateAcknowledgmentLookup(response.Acknowledgments);
                var unsupportedNamespaces = response.UnsupportedNamespaces.ToHashSet();
                foreach (var sent in sentKeys)
                {
                    if (unsupportedNamespaces.Contains(sent.Work.Namespace.Name))
                    {
                        CompleteUnsupported(sent.Work);
                        continue;
                    }

                    if (!acknowledgments.TryGetValue(
                        new(sent.Work.Namespace.Name, sent.Work.Key),
                        out var acknowledgedVersion))
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
                    if (completion.ImmediateRetry is { } retry)
                    {
                        pending.Enqueue(retry);
                    }
                }
            }

            return new(requiresBackoff, madeProgress);
        }

        private async ValueTask<DisseminationBroadcastResponse?> SendBatch(
            Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> valuesByNamespace,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _owner._sendGate.WaitAsync(cancellationToken);
                try
                {
                    var batch = new DisseminationBroadcastBatch
                    {
                        Sender = _owner._localSilo,
                        Values = valuesByNamespace,
                    };

                    var response = await GetTarget().PushBroadcast(batch, cancellationToken);
                    DisseminationInstruments.OnBroadcastSent(batch.Values, "tree");
                    return response;
                }
                finally
                {
                    _owner._sendGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDebugDisseminationSendFailed(_owner._logger, exception, Peer);
                return null;
            }
        }

        private IDisseminationSystemTarget GetTarget() =>
            _target ??= _owner._grainFactory.GetSystemTarget<IDisseminationSystemTarget>(
                Constants.DisseminationSystemTargetType,
                Peer);

        private List<PendingKeyWork> DrainDirtyUnsafe()
        {
            var result = new List<PendingKeyWork>(DirtyCount);
            foreach (var namespaceState in _statesByNamespace.Values)
            {
                foreach (var (key, keyState) in namespaceState.Keys)
                {
                    if (!keyState.Dirty)
                    {
                        continue;
                    }

                    keyState.Dirty = false;
                    keyState.InFlight = true;
                    namespaceState.DirtyCount--;
                    DirtyCount--;
                    result.Add(new(
                        namespaceState.Namespace,
                        key,
                        keyState.NotificationGeneration,
                        keyState.KnownVersion));
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
                if (!TryGetKeyStateUnsafe(work, out _, out var keyState))
                {
                    return;
                }

                if (keyState.KnownVersion is not { } knownVersion || version > knownVersion)
                {
                    keyState.KnownVersion = version;
                }

                keyState.InFlight = false;
            }
        }

        private AcknowledgmentCompletion CompleteAcknowledged(SentKey sent, long acknowledgedVersion)
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
                    ? acknowledgedVersion >= 0
                    : acknowledgedVersion > sent.FromVersion;
                if (keyState.NotificationGeneration != sent.Work.NotificationGeneration
                    || keyState.Dirty
                    || keyState.KnownVersion >= sent.ResolvedVersion)
                {
                    keyState.InFlight = false;
                    return new(null, RequiresBackoff: false, madeProgress);
                }

                if (madeProgress)
                {
                    return new(
                        sent.Work with { NotificationGeneration = keyState.NotificationGeneration, KnownVersion = keyState.KnownVersion },
                        RequiresBackoff: false,
                        MadeProgress: true);
                }

                keyState.InFlight = false;
                MarkDirtyUnsafe(namespaceState, keyState);
                return new(null, RequiresBackoff: true, MadeProgress: false);
            }
        }

        private bool CompleteFromExistingEvidence(SentKey sent)
        {
            lock (_lock)
            {
                if (!TryGetKeyStateUnsafe(sent.Work, out var namespaceState, out var keyState))
                {
                    return true;
                }

                if (keyState.KnownVersion >= sent.ResolvedVersion)
                {
                    keyState.InFlight = false;
                    return true;
                }

                keyState.InFlight = false;
                MarkDirtyUnsafe(namespaceState, keyState);
                return false;
            }
        }

        private void CompleteUnsupported(PendingKeyWork work)
        {
            lock (_lock)
            {
                if (!_statesByNamespace.TryGetValue(work.Namespace.Name, out var namespaceState))
                {
                    return;
                }

                DirtyCount -= namespaceState.DirtyCount;
                _statesByNamespace.Remove(work.Namespace.Name);
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
            lock (_lock)
            {
                if (TryGetKeyStateUnsafe(work, out _, out var keyState))
                {
                    keyState.InFlight = false;
                }
            }
        }

        private void Requeue(IEnumerable<PendingKeyWork> work)
        {
            lock (_lock)
            {
                if (_stopping)
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
                    if (TryGetKeyStateUnsafe(item, out var namespaceState, out var keyState))
                    {
                        keyState.InFlight = false;
                        MarkDirtyUnsafe(namespaceState, keyState);
                    }
                }
            }
        }

        private void ClearPendingUnsafe()
        {
            _statesByNamespace.Clear();
            DirtyCount = 0;
            _retryAttempt = 0;
            _wakeScheduled = false;
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
                && namespaceState.Keys.TryGetValue(work.Key, out keyState!))
            {
                return true;
            }

            namespaceState = null!;
            keyState = null!;
            return false;
        }

        private void MarkDirtyUnsafe(PeerNamespaceState namespaceState, PeerKeyState keyState)
        {
            if (keyState.Dirty)
            {
                return;
            }

            keyState.Dirty = true;
            namespaceState.DirtyCount++;
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
            if (repair.Status is not DisseminationRepairStatus.Produced
                || repair.Values.IsDefaultOrEmpty
                || repair.Version <= 0
                || request.ToVersion is { } requestedToVersion && repair.Version > requestedToVersion
                || repair.Values.Length > request.MaxItemCount)
            {
                return false;
            }

            var byteCount = 0;
            var expectedFromVersion = request.FromVersion;
            for (var i = 0; i < repair.Values.Length; i++)
            {
                var value = repair.Values[i];
                if (value.Key != request.Key
                    || value.FromVersion < 0
                    || value.ToVersion <= value.FromVersion
                    || value.ToVersion > repair.Version
                    || value.Payload.Length > request.MaxPayloadBytes)
                {
                    return false;
                }

                if (i == 0)
                {
                    if (expectedFromVersion is null && value.FromVersion != 0
                        || expectedFromVersion is { } fromVersion
                        && value.FromVersion != 0
                        && value.FromVersion != fromVersion)
                    {
                        return false;
                    }
                }
                else if (value.FromVersion != 0 && value.FromVersion != expectedFromVersion)
                {
                    return false;
                }

                expectedFromVersion = value.ToVersion;
                byteCount += value.Payload.Length;
                if (byteCount > request.MaxBatchBytes)
                {
                    return false;
                }
            }

            var lastVersion = repair.Values[^1].ToVersion;
            return repair.IsComplete ? lastVersion == repair.Version : lastVersion < repair.Version;
        }

        private sealed class PeerNamespaceState(IDisseminationNamespace disseminationNamespace)
        {
            public IDisseminationNamespace Namespace { get; } = disseminationNamespace;

            public Dictionary<DisseminationKey, PeerKeyState> Keys { get; } = [];

            public int DirtyCount { get; set; }

            public PeerKeyState GetOrCreateKey(DisseminationKey key)
            {
                if (!Keys.TryGetValue(key, out var result))
                {
                    result = new();
                    Keys.Add(key, result);
                }

                return result;
            }
        }

        private sealed class PeerKeyState
        {
            public long? KnownVersion { get; set; }

            public long NotificationGeneration { get; set; }

            public bool Dirty { get; set; }

            public bool InFlight { get; set; }
        }

        private readonly record struct PendingKeyWork(
            IDisseminationNamespace Namespace,
            DisseminationKey Key,
            long NotificationGeneration,
            long? KnownVersion);

        private readonly record struct SentKey(
            PendingKeyWork Work,
            long? FromVersion,
            long ResolvedVersion);

        private readonly record struct DigestKey(
            DisseminationNamespace Namespace,
            DisseminationKey Key);

        private readonly record struct AcknowledgmentCompletion(
            PendingKeyWork? ImmediateRetry,
            bool RequiresBackoff,
            bool MadeProgress);

        private readonly record struct SendWorkResult(bool RequiresBackoff, bool MadeProgress)
        {
            public static SendWorkResult None => default;
        }
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
}

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed class DisseminationBroadcastQueue(
    TimeProvider timeProvider,
    SiloAddress localSilo,
    IInternalGrainFactory grainFactory,
    IOptionsMonitor<DisseminationOptions> options,
    Action<SiloAddress, Exception> logSendFailed,
    Action<Exception> logFlushFailed)
{
    private readonly object _lock = new();
    private readonly Dictionary<SiloAddress, PeerQueuePump> _pendingBroadcast = [];
    private readonly SemaphoreSlim _sendGate = new(Math.Max(1, options.CurrentValue.MaxConcurrentSends));
    private bool _stopped;

    public void Enqueue(
        SiloAddress peer,
        DisseminationBroadcastValue item,
        IDisseminationNamespace disseminationNamespace)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_pendingBroadcast.TryGetValue(peer, out var pending))
            {
                pending = new PeerQueuePump(
                    peer,
                    localSilo,
                    grainFactory,
                    timeProvider,
                    options,
                    _sendGate,
                    logSendFailed,
                    logFlushFailed);
                _pendingBroadcast.Add(peer, pending);
            }

            pending.Enqueue(item, disseminationNamespace);
        }
    }

    public async Task FlushPendingBroadcast(CancellationToken cancellationToken)
    {
        List<PeerQueuePump> pending;
        lock (_lock)
        {
            pending = [.. _pendingBroadcast.Values.OrderBy(static batch => batch.Peer)];
        }

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.CurrentValue.MaxConcurrentSends,
                TaskScheduler = TaskScheduler.Current
            },
            async (batch, operationCancellationToken) => await batch.FlushAsync(operationCancellationToken));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        List<PeerQueuePump> pending;
        lock (_lock)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            pending = [.. _pendingBroadcast.Values.OrderBy(static batch => batch.Peer)];
            _pendingBroadcast.Clear();
        }

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.CurrentValue.MaxConcurrentSends,
                TaskScheduler = TaskScheduler.Current
            },
            async (batch, operationCancellationToken) => await batch.StopAsync(drain: true, operationCancellationToken));
    }

    public async Task Prune(
        DisseminationMembershipSnapshot membershipSnapshot,
        SiloAddress localSilo,
        CancellationToken cancellationToken)
    {
        List<PeerQueuePump>? removedPeers = null;
        lock (_lock)
        {
            foreach (var (peer, pending) in _pendingBroadcast)
            {
                if (localSilo.Equals(peer))
                {
                    continue;
                }

                if (!membershipSnapshot.ContainsMember(peer))
                {
                    (removedPeers ??= []).Add(pending);
                }
            }

            if (removedPeers is not null)
            {
                foreach (var pending in removedPeers)
                {
                    _pendingBroadcast.Remove(pending.Peer);
                }
            }
        }

        if (removedPeers is null)
        {
            return;
        }

        foreach (var pending in removedPeers)
        {
            await pending.StopAsync(drain: false, cancellationToken);
        }
    }

    private readonly record struct PendingNamespaceValues(DisseminationNamespace Namespace, ImmutableArray<DisseminationBroadcastValue> Values);

    private readonly record struct DigestKey(DisseminationNamespace Namespace, DisseminationKey Key);

    private sealed class PeerQueuePump(
        SiloAddress peer,
        SiloAddress localSilo,
        IInternalGrainFactory grainFactory,
        TimeProvider timeProvider,
        IOptionsMonitor<DisseminationOptions> options,
        SemaphoreSlim sendGate,
        Action<SiloAddress, Exception> logSendFailed,
        Action<Exception> logFlushFailed)
    {
        private readonly object _lock = new();
        private readonly SemaphoreSlim _flushLock = new(1, 1);
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly Dictionary<DisseminationNamespace, Dictionary<DisseminationKey, DisseminationBroadcastValue>> _valuesByNamespace = new();
        private DateTimeOffset? _flushAfter;
        private DateTimeOffset? _failureBackoffUntil;
        private CancellationTokenSource? _flushWakeup;
        private Task? _flushTask;
        private IDisseminationSystemTarget? _target;
        private bool _stopping;

        public SiloAddress Peer => peer;

        private int Count { get; set; }

        private int ByteCount { get; set; }

        public void Enqueue(DisseminationBroadcastValue item, IDisseminationNamespace disseminationNamespace)
        {
            var now = timeProvider.GetUtcNow();
            var key = new DigestKey(disseminationNamespace.Name, item.Value.Key);
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                if (TryGetValue(key, out var existing)
                    && existing.Value.ToVersion >= item.Value.ToVersion)
                {
                    return;
                }

                var flushAfter = now + disseminationNamespace.Options.MaxCoalescingDelay;
                if (_flushAfter is null || flushAfter < _flushAfter.Value)
                {
                    _flushAfter = flushAfter;
                    WakeupUnsafe();
                }

                AddOrReplace(key, item);
                var currentOptions = options.CurrentValue;
                if (Count >= currentOptions.MaxBatchItems
                    || ByteCount >= currentOptions.MaxBatchBytes
                    || GetNamespaceCount(disseminationNamespace.Name) >= disseminationNamespace.Options.MaxPendingItemCount)
                {
                    _flushAfter = now;
                    WakeupUnsafe();
                }

                StartFlushLoopUnsafe();
            }
        }

        public async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            await _flushLock.WaitAsync(cancellationToken);
            try
            {
                var values = DrainPendingBroadcast(force: true);
                if (!values.IsDefaultOrEmpty)
                {
                    await SendValues(values, cancellationToken);
                }
            }
            finally
            {
                _flushLock.Release();
            }

            Wakeup();
        }

        public async ValueTask StopAsync(bool drain, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_stopping)
                {
                    return;
                }

                _stopping = true;
                if (!drain)
                {
                    ClearPendingUnsafe();
                }

                _flushWakeup?.Cancel();
            }

            if (drain)
            {
                await FlushAsync(cancellationToken);
            }
            else
            {
                await _shutdownCts.CancelAsync();
            }

            if (_flushTask is { } flushTask)
            {
                try
                {
                    await flushTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                }
            }

            lock (_lock)
            {
                DisposeFlushWakeupUnsafe();
            }

            _flushLock.Dispose();
            _shutdownCts.Dispose();
        }

        private async Task RunScheduledFlush()
        {
            try
            {
                var cancellationToken = _shutdownCts.Token;
                while (true)
                {
                    var delay = GetDelayUntilNextFlush(out var wakeupToken);
                    if (delay is null)
                    {
                        return;
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        try
                        {
                            await Task.Delay(delay.Value, timeProvider, wakeupToken);
                        }
                        catch (OperationCanceledException) when (wakeupToken.IsCancellationRequested)
                        {
                            continue;
                        }
                    }

                    await _flushLock.WaitAsync(cancellationToken);
                    try
                    {
                        var values = DrainPendingBroadcast(force: false);
                        if (!values.IsDefaultOrEmpty)
                        {
                            await SendValues(values, cancellationToken);
                        }
                    }
                    finally
                    {
                        _flushLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                logFlushFailed(exception);
            }
            finally
            {
                var restart = false;
                lock (_lock)
                {
                    _flushTask = null;
                    DisposeFlushWakeupUnsafe();
                    restart = !_stopping && Count > 0;
                    if (restart)
                    {
                        StartFlushLoopUnsafe();
                    }
                }
            }
        }

        private TimeSpan? GetDelayUntilNextFlush(out CancellationToken wakeupToken)
        {
            lock (_lock)
            {
                if (_stopping || Count == 0 || _flushAfter is null)
                {
                    DisposeFlushWakeupUnsafe();
                    wakeupToken = CancellationToken.None;
                    return null;
                }

                var now = timeProvider.GetUtcNow();
                var next = _flushAfter.Value;
                if (_failureBackoffUntil is { } backoffUntil)
                {
                    if (backoffUntil > now)
                    {
                        next = backoffUntil > next ? backoffUntil : next;
                    }
                    else
                    {
                        _failureBackoffUntil = null;
                    }
                }

                if (next <= now)
                {
                    DisposeFlushWakeupUnsafe();
                    wakeupToken = CancellationToken.None;
                    return TimeSpan.Zero;
                }

                DisposeFlushWakeupUnsafe();
                _flushWakeup = new CancellationTokenSource();
                wakeupToken = _flushWakeup.Token;
                return next - now;
            }
        }

        private async ValueTask<bool> SendValues(
            ImmutableArray<PendingNamespaceValues> valuesByNamespace,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPeerBackedOff())
            {
                return false;
            }

            var currentBatch = new Dictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>.Builder>();
            var itemCount = 0;
            var byteCount = 0;
            foreach (var group in valuesByNamespace)
            {
                foreach (var item in group.Values)
                {
                    var currentOptions = options.CurrentValue;
                    if (itemCount > 0
                        && (itemCount >= currentOptions.MaxBatchItems
                            || byteCount + item.Value.Payload.Length > currentOptions.MaxBatchBytes))
                    {
                        if (!await SendBatch(currentBatch.ToFrozenDictionary(
                            static pair => pair.Key,
                            static pair => pair.Value.ToImmutable()), cancellationToken))
                        {
                            return false;
                        }

                        currentBatch.Clear();
                        itemCount = 0;
                        byteCount = 0;
                    }

                    ref var values = ref CollectionsMarshal.GetValueRefOrAddDefault(currentBatch, group.Namespace, out _);
                    (values ??= ImmutableArray.CreateBuilder<DisseminationBroadcastValue>()).Add(item);
                    itemCount++;
                    byteCount += item.Value.Payload.Length;
                }
            }

            if (itemCount > 0)
            {
                return await SendBatch(currentBatch.ToFrozenDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.ToImmutable()), cancellationToken);
            }

            return true;
        }

        private async ValueTask<bool> SendBatch(
            FrozenDictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>> valuesByNamespace,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPeerBackedOff())
            {
                return false;
            }

            try
            {
                await sendGate.WaitAsync(cancellationToken);
                try
                {
                    var batch = new DisseminationBroadcastBatch
                    {
                        Sender = localSilo,
                        Values = valuesByNamespace,
                    };

                    await GetTarget().PushBroadcast(batch, cancellationToken);
                    DisseminationInstruments.OnBroadcastSent(batch.Values, "tree");
                }
                finally
                {
                    sendGate.Release();
                }

                ClearBackoff();
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logSendFailed(peer, exception);
                SetBackoff();
                return false;
            }
        }

        private IDisseminationSystemTarget GetTarget() =>
            _target ??= grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer);

        private bool IsPeerBackedOff()
        {
            var now = timeProvider.GetUtcNow();
            lock (_lock)
            {
                if (_failureBackoffUntil is not { } until)
                {
                    return false;
                }

                if (until > now)
                {
                    return true;
                }

                _failureBackoffUntil = null;
                return false;
            }
        }

        private void SetBackoff()
        {
            lock (_lock)
            {
                _failureBackoffUntil = timeProvider.GetUtcNow() + options.CurrentValue.FailureBackoff;
                WakeupUnsafe();
            }
        }

        private void ClearBackoff()
        {
            lock (_lock)
            {
                _failureBackoffUntil = null;
            }
        }

        private void StartFlushLoopUnsafe()
        {
            if (_stopping)
            {
                return;
            }

            if (_flushTask is not { IsCompleted: false })
            {
                _flushTask = Task.Run(RunScheduledFlush);
            }
        }

        private void Wakeup()
        {
            lock (_lock)
            {
                WakeupUnsafe();
            }
        }

        private void WakeupUnsafe() => _flushWakeup?.Cancel();

        private ImmutableArray<PendingNamespaceValues> DrainPendingBroadcast(bool force)
        {
            var now = timeProvider.GetUtcNow();
            lock (_lock)
            {
                if (Count == 0 || _flushAfter is null || !force && _flushAfter > now)
                {
                    return [];
                }

                var result = ToImmutableValuesByNamespace();
                ClearPendingUnsafe();
                return result;
            }
        }

        private int GetNamespaceCount(DisseminationNamespace namespaceName) =>
            _valuesByNamespace.TryGetValue(namespaceName, out var values) ? values.Count : 0;

        private bool TryGetValue(DigestKey key, [NotNullWhen(true)] out DisseminationBroadcastValue? value)
        {
            if (_valuesByNamespace.TryGetValue(key.Namespace, out var namespaceValues))
            {
                return namespaceValues.TryGetValue(key.Key, out value!);
            }

            value = null;
            return false;
        }

        private void AddOrReplace(DigestKey key, DisseminationBroadcastValue value)
        {
            if (!_valuesByNamespace.TryGetValue(key.Namespace, out var namespaceValues))
            {
                namespaceValues = new Dictionary<DisseminationKey, DisseminationBroadcastValue>();
                _valuesByNamespace.Add(key.Namespace, namespaceValues);
            }

            if (namespaceValues.TryGetValue(key.Key, out var previous))
            {
                ByteCount -= previous.Value.Payload.Length;
            }
            else
            {
                Count++;
            }

            namespaceValues[key.Key] = value;
            ByteCount += value.Value.Payload.Length;
        }

        private ImmutableArray<PendingNamespaceValues> ToImmutableValuesByNamespace()
        {
            var result = ImmutableArray.CreateBuilder<PendingNamespaceValues>(_valuesByNamespace.Count);
            foreach (var (namespaceName, values) in _valuesByNamespace)
            {
                result.Add(new PendingNamespaceValues(namespaceName, [.. values.Values]));
            }

            return result.ToImmutable();
        }

        private void ClearPendingUnsafe()
        {
            _valuesByNamespace.Clear();
            _flushAfter = null;
            Count = 0;
            ByteCount = 0;
        }

        private void DisposeFlushWakeupUnsafe()
        {
            _flushWakeup?.Dispose();
            _flushWakeup = null;
        }
    }
}

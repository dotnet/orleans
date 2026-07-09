using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Internal;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationBroadcastQueue(
    TimeProvider timeProvider,
    SiloAddress localSilo,
    IInternalGrainFactory grainFactory,
    IOptionsMonitor<DisseminationOptions> options,
    IEnumerable<IDisseminationNamespace> disseminationNamespaces,
    ILogger<DisseminationBroadcastQueue> logger)
{
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly SiloAddress _localSilo = localSilo;
    private readonly IInternalGrainFactory _grainFactory = grainFactory;
    private readonly IOptionsMonitor<DisseminationOptions> _options = options;
    private readonly IDisseminationNamespace[] _disseminationNamespaces = [.. disseminationNamespaces];
    private readonly ILogger<DisseminationBroadcastQueue> _logger = logger;
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
                pending = new PeerQueuePump(peer, this);
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

        await Task.WhenAll(pending.Select(batch => batch.FlushAsync(cancellationToken).AsTask()));
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
            pending = [.. _pendingBroadcast.Values];
            _pendingBroadcast.Clear();
        }

        await Task.WhenAll(pending.Select(batch => batch.StopAsync(drain: true, cancellationToken).AsTask()));
    }

    public async Task Prune(
        DisseminationMembershipSnapshot membershipSnapshot,
        CancellationToken cancellationToken)
    {
        List<PeerQueuePump>? removedPeers = null;
        lock (_lock)
        {
            foreach (var (peer, pending) in _pendingBroadcast)
            {
                if (_localSilo.Equals(peer))
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

        await Task.WhenAll(removedPeers.Select(pending => pending.StopAsync(drain: false, cancellationToken).AsTask()));
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

    private sealed class PeerQueuePump
    {
        private readonly DisseminationBroadcastQueue _owner;
        private readonly object _lock = new();
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly WakeTimer _flushTimer;
        private readonly Task _flushTask;
        private Dictionary<DisseminationNamespace, Dictionary<DisseminationKey, DisseminationBroadcastValue>> _valuesByNamespace = [];
        private TaskCompletionSource _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _activeFlushCompletion;
        private IDisseminationSystemTarget? _target;
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

        private int Count { get; set; }

        private int ByteCount { get; set; }

        public void Enqueue(DisseminationBroadcastValue item, IDisseminationNamespace disseminationNamespace)
        {
            var namespaceName = disseminationNamespace.Name;
            var itemKey = item.Value.Key;
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                if (!_valuesByNamespace.TryGetValue(namespaceName, out var namespaceValues))
                {
                    namespaceValues = [];
                    _valuesByNamespace.Add(namespaceName, namespaceValues);
                }

                var wasEmpty = Count == 0;
                if (namespaceValues.GetValueOrDefault(itemKey) is { } existing)
                {
                    if (existing.Value.ToVersion >= item.Value.ToVersion)
                    {
                        return;
                    }

                    ByteCount -= existing.Value.Payload.Length;
                }
                else
                {
                    Count++;
                }

                namespaceValues[itemKey] = item;
                ByteCount += item.Value.Payload.Length;
                var currentOptions = _owner._options.CurrentValue;
                if (Count >= currentOptions.MaxBatchItems
                    || ByteCount >= currentOptions.MaxBatchBytes
                    || namespaceValues.Count >= disseminationNamespace.Options.MaxPendingItemCount)
                {
                    _flushTimer.Change(TimeSpan.Zero);
                }
                else if (wasEmpty)
                {
                    _flushTimer.Change(_owner.GetCoalescingDelay(disseminationNamespace.Options.MaxCoalescingDelay));
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
                if (Count > 0)
                {
                    flushCompletion = _nextFlushCompletion.Task;
                    wake = true;
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
                        if (Count > 0)
                        {
                            flushCompletion = _nextFlushCompletion.Task;
                            wake = true;
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
                    Dictionary<DisseminationNamespace, Dictionary<DisseminationKey, DisseminationBroadcastValue>> values;
                    lock (_lock)
                    {
                        if (Count == 0)
                        {
                            continue;
                        }

                        flushCompletion = _nextFlushCompletion;
                        _nextFlushCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _activeFlushCompletion = flushCompletion.Task;
                        values = DrainPendingBroadcastUnsafe();
                    }

                    try
                    {
                        await SendValues(values, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        LogDebugBroadcastFlushFailed(_owner._logger, exception);
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

        private async ValueTask SendValues(
            Dictionary<DisseminationNamespace, Dictionary<DisseminationKey, DisseminationBroadcastValue>> valuesByNamespace,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentOptions = _owner._options.CurrentValue;
            var currentBatch = new Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>>();
            var itemCount = 0;
            var byteCount = 0;
            foreach (var (namespaceName, namespaceValues) in valuesByNamespace)
            {
                foreach (var item in namespaceValues.Values)
                {
                    if (itemCount > 0
                        && (itemCount >= currentOptions.MaxBatchItems
                            || byteCount + item.Value.Payload.Length > currentOptions.MaxBatchBytes))
                    {
                        if (!await SendBatch(currentBatch, cancellationToken))
                        {
                            return;
                        }

                        currentBatch = new();
                        itemCount = 0;
                        byteCount = 0;
                    }

                    ref var values = ref CollectionsMarshal.GetValueRefOrAddDefault(currentBatch, namespaceName, out _);
                    (values ??= []).Add(item);
                    itemCount++;
                    byteCount += item.Value.Payload.Length;
                }
            }

            if (itemCount > 0)
            {
                await SendBatch(currentBatch, cancellationToken);
            }
        }

        private async ValueTask<bool> SendBatch(
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

                    await GetTarget().PushBroadcast(batch, cancellationToken);
                    DisseminationInstruments.OnBroadcastSent(batch.Values, "tree");
                }
                finally
                {
                    _owner._sendGate.Release();
                }

                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogDebugDisseminationSendFailed(_owner._logger, exception, Peer);
                return false;
            }
        }

        private IDisseminationSystemTarget GetTarget() =>
            _target ??= _owner._grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, Peer);

        private Dictionary<DisseminationNamespace, Dictionary<DisseminationKey, DisseminationBroadcastValue>> DrainPendingBroadcastUnsafe()
        {
            var result = _valuesByNamespace;
            _valuesByNamespace = [];
            Count = 0;
            ByteCount = 0;
            return result;
        }

        private void ClearPendingUnsafe()
        {
            _valuesByNamespace.Clear();
            Count = 0;
            ByteCount = 0;
        }
    }

    // Generate the send-failure log method used by broadcast peer pumps.
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination send to {Peer} failed.")]
    private static partial void LogDebugDisseminationSendFailed(ILogger logger, Exception exception, SiloAddress peer);

    // Generate the queue-flush log method used when a broadcast batch cannot be sent.
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination broadcast batch flush failed.")]
    private static partial void LogDebugBroadcastFlushFailed(ILogger logger, Exception exception);
}

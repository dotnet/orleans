using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationProtocol(
    IDisseminationTransport transport,
    IOptionsMonitor<DisseminationOptions> options,
    IEnumerable<IDisseminationTopic> topics,
    TimeProvider timeProvider,
    ILogger<DisseminationProtocol> logger) : IAsyncDisposable
{
    private readonly IDisseminationTransport _transport = transport;
    private readonly IOptionsMonitor<DisseminationOptions> _options = options;
    private readonly FrozenDictionary<string, IDisseminationTopic> _topics = topics.ToFrozenDictionary(static topic => topic.Name, StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger = logger;
    private readonly Dictionary<SiloAddress, DateTimeOffset> _failureBackoffUntil = [];
    private readonly object _failureLock = new();
    private readonly object _disposeLock = new();
    private readonly object _gossipQueueLock = new();
    private readonly object _peerCompatibilityLock = new();
    private readonly object _recentUpdateLock = new();
    private readonly object _topologyLock = new();
    private readonly SemaphoreSlim _gossipFlushLock = new(1, 1);
    private readonly CancellationTokenSource _gossipSendCts = new();
    private readonly Dictionary<SiloAddress, PendingGossipBatch> _pendingGossip = [];
    private readonly Dictionary<SiloAddress, Dictionary<string, DateTimeOffset>> _confirmedPeerTopics = [];
    private readonly Dictionary<DigestKey, DateTimeOffset> _lastUpdateReceivedAt = [];
    private DateTimeOffset? _nextGossipFlushAt;
    private CancellationTokenSource? _gossipFlushWakeup;
    private Task? _gossipFlushTask;
    private Task? _disposeTask;
    private bool _gossipFlushScheduled;
    private bool _stopping;
    private ParticipantTopology _activeMembersTopology = ParticipantTopology.Empty;
    private ParticipantTopology _allMembersTopology = ParticipantTopology.Empty;
    private long _antiEntropyRound;

    public FrozenDictionary<string, IDisseminationTopic> Topics => _topics;

    internal bool IsDisposed => _disposeTask?.IsCompletedSuccessfully == true;

    public async ValueTask<bool> Publish(
        string topicName,
        DisseminationValue item,
        IReadOnlyCollection<SiloAddress>? targetPeers,
        CancellationToken cancellationToken)
    {
        if (!TryGetEnabledTopic(topicName, out var topic))
        {
            return false;
        }

        if (GetPublishValidationFailureReason(topic, item) is { } reason)
        {
            await topic.OnFallbackRequired(peer: null, item.Digest, cancellationToken);
            DisseminationInstruments.OnFallback(topic.Name, reason);
            return false;
        }

        var root = item.Root;
        var topology = targetPeers is { Count: > 0 }
            ? BuildParticipantTopology(targetPeers, root, includeLocal: true)
            : await GetParticipantTopologyForRouting(topic.MembershipScope, root, cancellationToken);
        if (topology is null)
        {
            return false;
        }

        RecordRecentUpdate(topic.Name, item.Digest);
        var queued = true;
        foreach (var peer in GetOriginatorTreeTargets(topology, root))
        {
            queued &= EnqueueGossip(peer, item, topic);
        }

        return queued;
    }

    public async Task ReceiveGossip(DisseminationGossipBatch batch, CancellationToken cancellationToken)
    {
        RecordCompatiblePeer(batch.Sender, batch.ValuesByTopic.Keys);
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        DisseminationInstruments.OnGossipReceived(batch.ValuesByTopic, "tree");

        foreach (var (topicName, values) in batch.ValuesByTopic)
        {
            if (!TryGetEnabledTopic(topicName, out var topic))
            {
                continue;
            }

            foreach (var item in values)
            {
                await ApplyReceivedValue(topicName, topic, item, batch.Sender, forward: true, cancellationToken);
            }
        }
    }

    public AntiEntropyState CreateAntiEntropyState()
    {
        if (!_options.CurrentValue.Enabled)
        {
            return AntiEntropyState.Empty;
        }

        var topics = new Dictionary<string, AntiEntropyTopicState>(StringComparer.Ordinal);
        var currentValueStreams = new HashSet<DigestKey>();
        var now = _timeProvider.GetUtcNow();
        var round = Interlocked.Increment(ref _antiEntropyRound);
        foreach (var topic in _topics.Values)
        {
            if (!topic.IsEnabled)
            {
                continue;
            }

            List<DisseminationTopicDigest>? topicDigests = null;
            foreach (var topicDigest in topic.GetDigests())
            {
                var digestKey = new DigestKey(topic.Name, topicDigest.Key);
                currentValueStreams.Add(digestKey);
                if (ShouldRequestAntiEntropy(topic, digestKey, now))
                {
                    (topicDigests ??= []).Add(topicDigest);
                }
            }

            if (topicDigests is null)
            {
                continue;
            }

            topicDigests.Sort(static (left, right) =>
            {
                var result = string.Compare(left.Key, right.Key, StringComparison.Ordinal);
                return result != 0 ? result : left.Version.CompareTo(right.Version);
            });

            topics.Add(topic.Name, new AntiEntropyTopicState(
                SelectAntiEntropyPeers(topic.Name, topic.MembershipScope, round),
                [.. topicDigests]));
        }

        PruneRecentUpdates(currentValueStreams);
        return topics.Count == 0
            ? AntiEntropyState.Empty
            : new AntiEntropyState(topics.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public async Task<IReadOnlyList<DisseminationAntiEntropyResponse>> ExchangeAntiEntropy(
        AntiEntropyState state,
        CancellationToken cancellationToken)
    {
        if (state.Topics.Count == 0)
        {
            return [];
        }

        var requestsByPeer = new Dictionary<SiloAddress, Dictionary<string, ImmutableArray<DisseminationTopicDigest>>>();
        foreach (var (topicName, topicState) in state.Topics)
        {
            if (topicState.Peers.Length == 0 || topicState.Digests.Length == 0)
            {
                continue;
            }

            foreach (var peer in topicState.Peers)
            {
                if (!requestsByPeer.TryGetValue(peer, out var pendingRequest))
                {
                    pendingRequest = new Dictionary<string, ImmutableArray<DisseminationTopicDigest>>(StringComparer.Ordinal);
                    requestsByPeer.Add(peer, pendingRequest);
                }

                pendingRequest[topicName] = topicState.Digests;
            }
        }

        var responses = new List<DisseminationAntiEntropyResponse>(requestsByPeer.Count);
        var retainedItemCount = 0;
        var retainedByteCount = 0;
        var options = _options.CurrentValue;
        foreach (var (peer, pendingRequest) in requestsByPeer.OrderBy(static entry => entry.Key))
        {
            foreach (var digestsByTopic in CreateAntiEntropyRequests(pendingRequest))
            {
                if (retainedItemCount >= options.MaxBatchItems || retainedByteCount >= options.MaxBatchBytes)
                {
                    return responses;
                }

                var request = new DisseminationAntiEntropyRequest
                {
                    Sender = _transport.LocalSilo,
                    DigestsByTopic = digestsByTopic,
                };

                var response = await SafeRequest(
                    peer,
                    cancellationToken,
                    target => _transport.ExchangeAntiEntropy(target, request, cancellationToken));
                if (response is null)
                {
                    continue;
                }

                SetCompatiblePeerTopics(peer, response.SupportedTopics);
                DisseminationInstruments.OnAntiEntropyExchange(
                    "out",
                    GetDigestCount(request.DigestsByTopic),
                    GetValueCount(response.ValuesByTopic),
                    response.Truncated);
                var limitedResponse = LimitAntiEntropyResponse(
                    response,
                    options.MaxBatchItems - retainedItemCount,
                    options.MaxBatchBytes - retainedByteCount,
                    out var roundLimitReached);
                if (limitedResponse is not null)
                {
                    responses.Add(limitedResponse);
                    retainedItemCount += GetValueCount(limitedResponse.ValuesByTopic);
                    retainedByteCount += GetValueByteCount(limitedResponse.ValuesByTopic);
                }

                if (roundLimitReached)
                {
                    return responses;
                }
            }
        }

        return responses;
    }

    public async Task ApplyAntiEntropyResponses(
        IReadOnlyList<DisseminationAntiEntropyResponse> responses,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        foreach (var response in responses)
        {
            foreach (var (topicName, values) in response.ValuesByTopic)
            {
                if (!TryGetEnabledTopic(topicName, out var topic))
                {
                    continue;
                }

                foreach (var item in values)
                {
                    try
                    {
                        await ApplyReceivedValue(topicName, topic, item, response.Sender, forward: false, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        LogDebugAntiEntropyRepairValueFailed(_logger, exception, response.Sender, topicName, item.Digest.Key, item.Digest.Version);
                    }
                }
            }
        }
    }

    public async ValueTask<DisseminationAntiEntropyResponse> ReceiveAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken)
    {
        RecordCompatiblePeer(request.Sender, request.DigestsByTopic.Keys);
        if (!_options.CurrentValue.Enabled)
        {
            return CreateAntiEntropyResponse(ImmutableDictionary<string, ImmutableArray<DisseminationValue>>.Empty, truncated: false);
        }

        var requestDigestCount = GetDigestCount(request.DigestsByTopic);
        var values = new List<TopicValue>();
        var byteCount = 0;
        var truncated = false;
        var options = _options.CurrentValue;

        foreach (var (topicName, remoteTopicDigests) in request.DigestsByTopic)
        {
            if (!TryGetEnabledTopic(topicName, out var requestedTopic))
            {
                continue;
            }

            var remoteDigests = GetRemoteDigestMap(requestedTopic, remoteTopicDigests);
            if (remoteDigests.Count == 0)
            {
                continue;
            }

            foreach (var topicDigest in requestedTopic.GetDigests())
            {
                if (!remoteDigests.TryGetValue(topicDigest.Key, out var remoteDigest))
                {
                    continue;
                }

                var localDigest = topicDigest;
                if (requestedTopic.CompareVersion(localDigest, remoteDigest) <= 0)
                {
                    continue;
                }

                var item = await requestedTopic.GetValue(
                    localDigest,
                    remoteDigest,
                    cancellationToken);
                if (item is null
                    || !ValidatePayloadSize(requestedTopic.Name, requestedTopic, item))
                {
                    continue;
                }

                if (values.Count >= options.MaxBatchItems || byteCount + item.Payload.Length > options.MaxBatchBytes)
                {
                    truncated = true;
                    break;
                }

                values.Add(new TopicValue(requestedTopic.Name, item));
                byteCount += item.Payload.Length;
            }

            if (truncated)
            {
                break;
            }
        }

        DisseminationInstruments.OnAntiEntropyExchange("in", requestDigestCount, values.Count, truncated);
        return CreateAntiEntropyResponse(GroupValuesByTopic(values), truncated);
    }

    private async ValueTask<DisseminationApplyResult> ApplyReceivedValue(
        string topicName,
        IDisseminationTopic topic,
        DisseminationValue value,
        SiloAddress sender,
        bool forward,
        CancellationToken cancellationToken)
    {
        if (!ValidatePayloadSize(topicName, topic, value))
        {
            return DisseminationApplyResult.Rejected;
        }

        if (IsExpired(value) || topic.IsObsolete(value.Digest))
        {
            EmitApplyResult(topicName, value, sender, DisseminationApplyResult.Obsolete);
            return DisseminationApplyResult.Obsolete;
        }

        var result = await topic.ApplyValue(value, cancellationToken);
        EmitApplyResult(topicName, value, sender, result);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            RecordRecentUpdate(topicName, value.Digest);
        }

        if (result == DisseminationApplyResult.Applied && forward)
        {
            await Forward(value, topic, sender, cancellationToken);
        }

        return result;
    }

    private async Task Forward(DisseminationValue item, IDisseminationTopic topic, SiloAddress sender, CancellationToken cancellationToken)
    {
        var root = item.Root;
        var topology = await GetParticipantTopologyForRouting(topic.MembershipScope, root, cancellationToken);
        if (topology is null)
        {
            return;
        }

        foreach (var peer in GetForwardingTreeTargets(topology, root, sender))
        {
            EnqueueGossip(peer, item, topic);
        }
    }

    internal async Task FlushPendingGossip(CancellationToken cancellationToken)
    {
        await _gossipFlushLock.WaitAsync(cancellationToken);
        try
        {
            var batches = DrainPendingGossip(force: true);
            CancelScheduledGossipFlushDelay();
            await SendGossipBatches(batches, cancellationToken);
        }
        finally
        {
            _gossipFlushLock.Release();
        }
    }

    internal IReadOnlyList<SiloAddress> GetUnconfirmedPeers(
        string topicName,
        DisseminationMembershipScope membershipScope,
        IReadOnlyCollection<SiloAddress>? candidates = null)
    {
        var membership = _transport.GetMembership();
        var participants = membershipScope == DisseminationMembershipScope.ActiveMembers
            ? membership.ActiveMembers
            : membership.AllMembers;
        var participantSet = participants.ToHashSet();
        var now = _timeProvider.GetUtcNow();
        var confirmationTtl = GetPeerTopicConfirmationTtl(topicName);
        lock (_peerCompatibilityLock)
        {
            foreach (var peer in _confirmedPeerTopics.Keys.Where(peer => !participantSet.Contains(peer)).ToArray())
            {
                _confirmedPeerTopics.Remove(peer);
            }

            return participants
                .Where(peer => !Equals(peer, _transport.LocalSilo)
                    && (candidates is null || candidates.Contains(peer))
                    && (!_confirmedPeerTopics.TryGetValue(peer, out var topics)
                        || !topics.TryGetValue(topicName, out var confirmedAt)
                        || now - confirmedAt >= confirmationTtl))
                .ToArray();
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? scheduledFlush;
        lock (_gossipQueueLock)
        {
            _stopping = true;
            _gossipFlushWakeup?.Cancel();
            scheduledFlush = _gossipFlushTask;
        }

        using var cancellationRegistration = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _gossipSendCts);
        try
        {
            if (scheduledFlush is not null)
            {
                await scheduledFlush.WaitAsync(cancellationToken);
            }

            await FlushPendingGossip(cancellationToken);
        }
        finally
        {
            _gossipSendCts.Cancel();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task? scheduledFlush;
        lock (_gossipQueueLock)
        {
            _stopping = true;
            _gossipFlushWakeup?.Cancel();
            scheduledFlush = _gossipFlushTask;
        }

        _gossipSendCts.Cancel();
        if (scheduledFlush is not null)
        {
            await scheduledFlush;
        }

        await _gossipFlushLock.WaitAsync();
        lock (_gossipQueueLock)
        {
            DisposeGossipFlushWakeupUnsafe();
            _pendingGossip.Clear();
        }

        _gossipSendCts.Dispose();
        _gossipFlushLock.Dispose();
    }

    private bool EnqueueGossip(SiloAddress peer, DisseminationValue item, IDisseminationTopic topic)
    {
        var now = _timeProvider.GetUtcNow();
        var key = new DigestKey(topic.Name, item.Digest.Key);
        lock (_gossipQueueLock)
        {
            if (!_pendingGossip.TryGetValue(peer, out var pending))
            {
                pending = new PendingGossipBatch(now + topic.Options.MaxCoalescingDelay);
                _pendingGossip.Add(peer, pending);
            }
            else if (pending.TryGetValue(key, out var existing)
                && topic.CompareVersion(existing.Digest, item.Digest) >= 0)
            {
                return true;
            }
            else if (now + topic.Options.MaxCoalescingDelay < pending.FlushAfter)
            {
                pending.FlushAfter = now + topic.Options.MaxCoalescingDelay;
            }

            if (!pending.TryAddOrReplace(
                key,
                item,
                topic.Options.MaxPendingItemCount,
                _options.CurrentValue.MaxBatchItems,
                _options.CurrentValue.MaxBatchBytes))
            {
                return false;
            }

            if (pending.Count >= _options.CurrentValue.MaxBatchItems
                || pending.ByteCount >= _options.CurrentValue.MaxBatchBytes
                || pending.GetTopicCount(topic.Name) >= topic.Options.MaxPendingItemCount)
            {
                pending.FlushAfter = now;
            }
        }

        ScheduleGossipFlush();
        return true;
    }

    private void ScheduleGossipFlush()
    {
        lock (_gossipQueueLock)
        {
            if (_pendingGossip.Count == 0)
            {
                return;
            }

            if (_stopping)
            {
                return;
            }

            var next = GetNextPendingGossipFlushUnsafe();
            if (!_gossipFlushScheduled)
            {
                _gossipFlushScheduled = true;
                _nextGossipFlushAt = next;
                _gossipFlushTask = Task.Run(RunScheduledGossipFlush);
            }
            else if (_nextGossipFlushAt is null || next < _nextGossipFlushAt.Value)
            {
                _nextGossipFlushAt = next;
                _gossipFlushWakeup?.Cancel();
            }
        }
    }

    private async Task RunScheduledGossipFlush()
    {
        try
        {
            while (true)
            {
                var delay = GetDelayUntilNextGossipFlush(out var wakeupToken);
                if (delay is null)
                {
                    return;
                }

                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay.Value, _timeProvider, wakeupToken);
                    }
                    catch (OperationCanceledException) when (wakeupToken.IsCancellationRequested)
                    {
                        lock (_gossipQueueLock)
                        {
                            if (_stopping)
                            {
                                return;
                            }
                        }

                        continue;
                    }
                }

                await _gossipFlushLock.WaitAsync(_gossipSendCts.Token);
                try
                {
                    var batches = DrainPendingGossip(force: false);
                    await SendGossipBatches(batches, _gossipSendCts.Token);
                }
                finally
                {
                    _gossipFlushLock.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_gossipSendCts.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogDebugGossipFlushFailed(_logger, exception);
        }
        finally
        {
            var reschedule = false;
            lock (_gossipQueueLock)
            {
                _gossipFlushScheduled = false;
                _nextGossipFlushAt = null;
                DisposeGossipFlushWakeupUnsafe();
                _gossipFlushTask = null;
                reschedule = !_stopping && _pendingGossip.Count > 0;
            }

            if (reschedule)
            {
                ScheduleGossipFlush();
            }
        }
    }

    private TimeSpan? GetDelayUntilNextGossipFlush(out CancellationToken wakeupToken)
    {
        lock (_gossipQueueLock)
        {
            if (_stopping || _pendingGossip.Count == 0)
            {
                _nextGossipFlushAt = null;
                DisposeGossipFlushWakeupUnsafe();
                wakeupToken = CancellationToken.None;
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            var next = GetNextPendingGossipFlushUnsafe();
            _nextGossipFlushAt = next;
            if (next <= now)
            {
                DisposeGossipFlushWakeupUnsafe();
                wakeupToken = CancellationToken.None;
                return TimeSpan.Zero;
            }

            DisposeGossipFlushWakeupUnsafe();
            _gossipFlushWakeup = CancellationTokenSource.CreateLinkedTokenSource(_gossipSendCts.Token);
            wakeupToken = _gossipFlushWakeup.Token;
            return next - now;
        }
    }

    private void CancelScheduledGossipFlushDelay()
    {
        lock (_gossipQueueLock)
        {
            _gossipFlushWakeup?.Cancel();
        }
    }

    private DateTimeOffset GetNextPendingGossipFlushUnsafe() => _pendingGossip.Values.Min(static pending => pending.FlushAfter);

    private List<(SiloAddress Peer, ImmutableArray<PendingTopicValues> ValuesByTopic)> DrainPendingGossip(bool force)
    {
        var now = _timeProvider.GetUtcNow();
        var result = new List<(SiloAddress Peer, ImmutableArray<PendingTopicValues> ValuesByTopic)>();
        lock (_gossipQueueLock)
        {
            foreach (var (peer, pending) in _pendingGossip)
            {
                if (!force && pending.FlushAfter > now)
                {
                    continue;
                }

                _pendingGossip.Remove(peer);
                result.Add((peer, pending.ToImmutableValuesByTopic()));
            }
        }

        result.Sort(static (left, right) => left.Peer.CompareTo(right.Peer));
        return result;
    }

    private async Task SendGossipBatches(List<(SiloAddress Peer, ImmutableArray<PendingTopicValues> ValuesByTopic)> batches, CancellationToken cancellationToken)
    {
        var nextBatch = -1;
        var workers = new Task[Math.Min(batches.Count, _options.CurrentValue.MaxConcurrentSends)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = SendNextBatches();
        }

        await Task.WhenAll(workers);

        async Task SendNextBatches()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextBatch);
                if (index >= batches.Count)
                {
                    return;
                }

                var queued = batches[index];
                await SendGossipBatch(queued.Peer, queued.ValuesByTopic, cancellationToken);
            }
        }
    }

    private async Task SendGossipBatch(SiloAddress peer, IReadOnlyList<PendingTopicValues> valuesByTopic, CancellationToken cancellationToken)
    {
        var currentBatch = new Dictionary<string, ImmutableArray<DisseminationValue>.Builder>(StringComparer.Ordinal);
        var itemCount = 0;
        var byteCount = 0;
        foreach (var group in valuesByTopic)
        {
            if (!TryGetEnabledTopic(group.Topic, out var topic))
            {
                continue;
            }

            foreach (var item in group.Values)
            {
                if (IsExpired(item) || topic.IsObsolete(item.Digest))
                {
                    continue;
                }

                if (itemCount > 0
                    && (itemCount >= _options.CurrentValue.MaxBatchItems
                        || byteCount + item.Payload.Length > _options.CurrentValue.MaxBatchBytes))
                {
                    await SendGossipBatchCore(peer, CreateValueGroups(currentBatch), cancellationToken);
                    currentBatch.Clear();
                    itemCount = 0;
                    byteCount = 0;
                }

                AddToValueGroups(currentBatch, group.Topic, item);
                itemCount++;
                byteCount += item.Payload.Length;
            }
        }

        if (itemCount > 0)
        {
            await SendGossipBatchCore(peer, CreateValueGroups(currentBatch), cancellationToken);
        }
    }

    private async Task SendGossipBatchCore(
        SiloAddress peer,
        ImmutableDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic,
        CancellationToken cancellationToken)
    {
        var batch = new DisseminationGossipBatch
        {
            Sender = _transport.LocalSilo,
            ValuesByTopic = valuesByTopic,
        };

        var sent = await SafeSend(
            peer,
            cancellationToken,
            target => _transport.SendGossip(target, batch, cancellationToken));
        if (sent)
        {
            DisseminationInstruments.OnGossipSent(batch.ValuesByTopic, "tree");
        }
    }

    private async ValueTask<bool> SafeSend(SiloAddress peer, CancellationToken cancellationToken, Func<SiloAddress, Task> send)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsPeerBackedOff(peer))
        {
            return false;
        }

        try
        {
            await send(peer);
            cancellationToken.ThrowIfCancellationRequested();
            ClearPeerBackoff(peer);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogDebugDisseminationSendFailed(_logger, exception, peer);
            SetPeerBackoff(peer);
            return false;
        }
    }

    private async ValueTask<DisseminationAntiEntropyResponse?> SafeRequest(
        SiloAddress peer,
        CancellationToken cancellationToken,
        Func<SiloAddress, ValueTask<DisseminationAntiEntropyResponse>> request)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsPeerBackedOff(peer))
        {
            return null;
        }

        try
        {
            var response = await request(peer);
            cancellationToken.ThrowIfCancellationRequested();
            ClearPeerBackoff(peer);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogDebugDisseminationSendFailed(_logger, exception, peer);
            SetPeerBackoff(peer);
            return null;
        }
    }

    private Dictionary<string, DisseminationTopicDigest> GetRemoteDigestMap(
        IDisseminationTopic topic,
        ImmutableArray<DisseminationTopicDigest> digests)
    {
        var result = new Dictionary<string, DisseminationTopicDigest>(digests.Length, StringComparer.Ordinal);
        foreach (var topicDigest in digests)
        {
            if (!result.TryGetValue(topicDigest.Key, out var existing) || topic.CompareVersion(topicDigest, existing) > 0)
            {
                result[topicDigest.Key] = topicDigest;
            }
        }

        return result;
    }

    private bool ShouldRequestAntiEntropy(IDisseminationTopic topic, DigestKey key, DateTimeOffset now)
    {
        lock (_recentUpdateLock)
        {
            return !_lastUpdateReceivedAt.TryGetValue(key, out var lastReceived)
                || now - lastReceived >= topic.Options.ExpectedUpdateCadence;
        }
    }

    private void RecordRecentUpdate(string topicName, DisseminationTopicDigest digest)
    {
        var key = new DigestKey(topicName, digest.Key);
        lock (_recentUpdateLock)
        {
            _lastUpdateReceivedAt[key] = _timeProvider.GetUtcNow();
        }
    }

    private void PruneRecentUpdates(HashSet<DigestKey> currentValueStreams)
    {
        lock (_recentUpdateLock)
        {
            foreach (var key in _lastUpdateReceivedAt.Keys)
            {
                if (!currentValueStreams.Contains(key))
                {
                    _lastUpdateReceivedAt.Remove(key);
                }
            }
        }
    }

    private ImmutableArray<SiloAddress> SelectAntiEntropyPeers(string topicName, DisseminationMembershipScope membershipScope, long round)
    {
        var options = _options.CurrentValue.Overlay;
        var topology = GetParticipantTopology(membershipScope);
        var participants = topology.Participants;
        if (participants.Length <= 1)
        {
            return [];
        }

        if (!topology.Indices.TryGetValue(_transport.LocalSilo, out var localIndex))
        {
            return [];
        }

        var fanout = GetFanOutFactor(participants.Length);
        var candidates = new List<(SiloAddress Peer, ulong Score)>();
        foreach (var index in GetAntiEntropyCandidateIndexes(localIndex, participants.Length, fanout))
        {
            if (index != localIndex)
            {
                var peer = participants[index];
                candidates.Add((peer, GetRepairPeerScore(peer, topicName, round, localIndex)));
            }
        }

        var count = Math.Min(options.AntiEntropyPeerCount, candidates.Count);
        if (count <= 0)
        {
            return [];
        }

        candidates.Sort(static (left, right) =>
        {
            var result = left.Score.CompareTo(right.Score);
            return result != 0 ? result : left.Peer.CompareTo(right.Peer);
        });

        var result = ImmutableArray.CreateBuilder<SiloAddress>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(candidates[i].Peer);
        }

        return result.MoveToImmutable();
    }

    private static IEnumerable<int> GetAntiEntropyCandidateIndexes(int localIndex, int participantCount, int fanout)
    {
        if (participantCount <= 1)
        {
            yield break;
        }

        var topLevelEnd = Math.Min(fanout, participantCount);
        if (localIndex < topLevelEnd)
        {
            for (var i = 0; i < topLevelEnd; i++)
            {
                yield return i;
            }

            yield break;
        }

        var parentIndex = localIndex / fanout - 1;
        if (parentIndex < 0)
        {
            yield break;
        }

        var (previousLevelStart, previousLevelEnd) = GetLevelRange(parentIndex, participantCount, fanout);
        var windowStart = previousLevelStart + (parentIndex - previousLevelStart) / fanout * fanout;
        var windowEnd = Math.Min(previousLevelEnd, windowStart + fanout);
        for (var i = windowStart; i < windowEnd; i++)
        {
            yield return i;
        }
    }

    private static (int Start, int End) GetLevelRange(int index, int participantCount, int fanout)
    {
        var start = 0L;
        var width = (long)fanout;
        while (index >= start + width && start + width < participantCount)
        {
            start += width;
            width = Math.Min(width * fanout, participantCount - start);
        }

        return ((int)start, (int)Math.Min(participantCount, start + width));
    }

    private static ulong GetRepairPeerScore(SiloAddress peer, string topicName, long round, int localIndex)
    {
        var value = (ulong)(uint)peer.GetConsistentHashCode();
        value ^= Mix(GetStableStringHash(topicName));
        value ^= (ulong)round * 0x9E3779B97F4A7C15UL;
        value ^= (ulong)(uint)localIndex << 32;
        return Mix(value);
    }

    private static ulong GetStableStringHash(string value)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        foreach (var ch in value)
        {
            hash ^= (byte)ch;
            hash *= prime;
            hash ^= (byte)(ch >> 8);
            hash *= prime;
        }

        return hash;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        value ^= value >> 31;
        return value;
    }

    private int GetFanOutFactor(int participantCount)
    {
        if (participantCount <= 1)
        {
            return 1;
        }

        var overlay = _options.CurrentValue.Overlay;
        var fanout = overlay.FanOutFactor?.Invoke(participantCount) ?? GetConfiguredFanOutFactor(overlay, participantCount);
        return Math.Clamp(fanout, 1, participantCount);
    }

    private static int GetConfiguredFanOutFactor(DisseminationOverlayOptions options, int participantCount)
    {
        var count = Math.Max(1, participantCount);
        var targetHopCount = Math.Max(1, options.TargetHopCount);
        var scaled = targetHopCount switch
        {
            1 => count,
            2 => Math.Sqrt(count),
            3 => Math.Cbrt(count),
            _ => Math.Pow(count, 1d / targetHopCount),
        };
        var min = Math.Max(1, options.MinFanOutFactor);
        var max = Math.Max(min, options.MaxFanOutFactor);
        return (int)Math.Ceiling(Math.Max(min, Math.Min(scaled, max)));
    }

    private IReadOnlyList<SiloAddress> GetOriginatorTreeTargets(ParticipantTopology topology, SiloAddress root)
    {
        if (!topology.Indices.TryGetValue(root, out var rootIndex))
        {
            return [];
        }

        var fanout = GetFanOutFactor(topology.Participants.Length);
        var result = new List<SiloAddress>(Math.Min(fanout * 2, topology.Participants.Length));
        AddTopLevelTargets(topology, fanout, root, result);
        AddFixedChildren(topology, rootIndex, fanout, root, except: null, result);
        return result;
    }

    private IReadOnlyList<SiloAddress> GetForwardingTreeTargets(ParticipantTopology topology, SiloAddress root, SiloAddress sender)
    {
        if (!topology.Indices.TryGetValue(_transport.LocalSilo, out var localIndex))
        {
            return [];
        }

        var fanout = GetFanOutFactor(topology.Participants.Length);
        var result = new List<SiloAddress>(Math.Min(fanout, topology.Participants.Length));
        AddFixedChildren(topology, localIndex, fanout, root, sender, result);
        return result;
    }

    private static void AddTopLevelTargets(
        ParticipantTopology topology,
        int fanout,
        SiloAddress root,
        List<SiloAddress> result)
    {
        var count = Math.Min(fanout, topology.Participants.Length);
        for (var i = 0; i < count; i++)
        {
            AddTarget(topology.Participants[i], root, except: null, result);
        }
    }

    private static void AddFixedChildren(
        ParticipantTopology topology,
        int index,
        int fanout,
        SiloAddress root,
        SiloAddress? except,
        List<SiloAddress> result)
    {
        var firstChild = (long)fanout * (index + 1);
        for (var i = 0; i < fanout; i++)
        {
            var childIndex = firstChild + i;
            if (childIndex >= topology.Participants.Length)
            {
                break;
            }

            AddTarget(topology.Participants[(int)childIndex], root, except, result);
        }
    }

    private static void AddTarget(SiloAddress peer, SiloAddress root, SiloAddress? except, List<SiloAddress> result)
    {
        if (Equals(peer, root) || except is { } excluded && Equals(peer, excluded) || result.Contains(peer))
        {
            return;
        }

        result.Add(peer);
    }

    private ParticipantTopology GetParticipantTopology(DisseminationMembershipScope membershipScope)
    {
        var membership = _transport.GetMembership();
        PrunePeerState(membership);
        return membershipScope == DisseminationMembershipScope.AllMembers
            ? GetCachedParticipantTopology(membership.AllMembers, ref _allMembersTopology)
            : GetCachedParticipantTopology(membership.ActiveMembers, ref _activeMembersTopology);
    }

    private void PrunePeerState(DisseminationMembership membership)
    {
        HashSet<SiloAddress>? currentParticipants = null;
        var now = _timeProvider.GetUtcNow();

        lock (_failureLock)
        {
            foreach (var (peer, until) in _failureBackoffUntil)
            {
                if (until <= now || !IsCurrentParticipant(peer))
                {
                    _failureBackoffUntil.Remove(peer);
                }
            }
        }

        lock (_gossipQueueLock)
        {
            foreach (var peer in _pendingGossip.Keys)
            {
                if (!IsCurrentParticipant(peer))
                {
                    _pendingGossip.Remove(peer);
                }
            }

            if (_pendingGossip.Count == 0)
            {
                _nextGossipFlushAt = null;
                _gossipFlushWakeup?.Cancel();
            }
        }

        bool IsCurrentParticipant(SiloAddress peer)
        {
            currentParticipants ??= CreateCurrentParticipantSet();
            return currentParticipants.Contains(peer);
        }

        HashSet<SiloAddress> CreateCurrentParticipantSet()
        {
            var result = new HashSet<SiloAddress>(membership.AllMembers);
            result.UnionWith(membership.ActiveMembers);
            result.Add(_transport.LocalSilo);
            return result;
        }
    }

    private void DisposeGossipFlushWakeupUnsafe()
    {
        _gossipFlushWakeup?.Dispose();
        _gossipFlushWakeup = null;
    }

    private ParticipantTopology GetCachedParticipantTopology(
        IEnumerable<SiloAddress> participants,
        ref ParticipantTopology cachedTopology)
    {
        var orderedParticipants = GetOrderedParticipants(participants, root: null, includeLocal: true, preserveOrder: true);

        lock (_topologyLock)
        {
            if (cachedTopology.Participants.SequenceEqual(orderedParticipants))
            {
                return cachedTopology;
            }

            var updated = BuildParticipantTopology(orderedParticipants);
            cachedTopology = updated;
            return updated;
        }
    }

    private ParticipantTopology BuildParticipantTopology(
        IEnumerable<SiloAddress> participants,
        SiloAddress? root,
        bool includeLocal) =>
        BuildParticipantTopology(GetOrderedParticipants(participants, root, includeLocal, preserveOrder: false));

    private ImmutableArray<SiloAddress> GetOrderedParticipants(
        IEnumerable<SiloAddress> participants,
        SiloAddress? root,
        bool includeLocal,
        bool preserveOrder)
    {
        var orderedParticipants = new List<SiloAddress>();
        var seen = new HashSet<SiloAddress>();
        foreach (var participant in participants)
        {
            AddParticipant(participant);
        }

        if (includeLocal)
        {
            AddParticipant(_transport.LocalSilo);
        }

        if (root is { } rootAddress)
        {
            AddParticipant(rootAddress);
        }

        if (!preserveOrder)
        {
            orderedParticipants.Sort(static (left, right) => left.CompareTo(right));
        }

        return [.. orderedParticipants];

        void AddParticipant(SiloAddress participant)
        {
            if (seen.Add(participant))
            {
                orderedParticipants.Add(participant);
            }
        }
    }

    private static ParticipantTopology BuildParticipantTopology(ImmutableArray<SiloAddress> participants)
    {
        var indices = new Dictionary<SiloAddress, int>(participants.Length);
        for (var i = 0; i < participants.Length; i++)
        {
            indices[participants[i]] = i;
        }

        return new ParticipantTopology(participants, indices.ToFrozenDictionary());
    }

    private async ValueTask<ParticipantTopology?> GetParticipantTopologyForRouting(
        DisseminationMembershipScope membershipScope,
        SiloAddress root,
        CancellationToken cancellationToken)
    {
        var topology = GetParticipantTopology(membershipScope);
        if (topology.Indices.ContainsKey(root))
        {
            return topology;
        }

        LogDebugDisseminationRootMissing(_logger, root);
        await _transport.RefreshMembership(cancellationToken);
        topology = GetParticipantTopology(membershipScope);
        return topology.Indices.ContainsKey(root) ? topology : null;
    }

    private bool TryGetEnabledTopic(string topicName, out IDisseminationTopic topic)
    {
        if (_options.CurrentValue.Enabled
            && _topics.TryGetValue(topicName, out topic!)
            && topic.IsEnabled)
        {
            return true;
        }

        topic = default!;
        return false;
    }

    private bool ValidatePayloadSize(string topicName, IDisseminationTopic topic, DisseminationValue item)
    {
        var options = _options.CurrentValue;
        if (item.Payload.Length > topic.Options.MaxPayloadBytes || item.Payload.Length > options.MaxBatchBytes)
        {
            DisseminationEvents.EmitPayloadDrop(topicName, item.Digest, _transport.LocalSilo, "oversize", item.Payload.Length);
            DisseminationInstruments.OnPayloadDropped(topicName, "oversize");
            return false;
        }

        return true;
    }

    private string? GetPublishValidationFailureReason(IDisseminationTopic topic, DisseminationValue item)
    {
        if (IsExpired(item))
        {
            return "expired";
        }

        if (topic.IsObsolete(item.Digest))
        {
            return "obsolete";
        }

        return ValidatePayloadSize(topic.Name, topic, item) ? null : "oversize";
    }

    private bool IsExpired(DisseminationValue item) => item.ExpiresAt <= _timeProvider.GetUtcNow();

    private bool IsPeerBackedOff(SiloAddress peer)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_failureLock)
        {
            if (!_failureBackoffUntil.TryGetValue(peer, out var until))
            {
                return false;
            }

            if (until > now)
            {
                return true;
            }

            _failureBackoffUntil.Remove(peer);
            return false;
        }
    }

    private void ClearPeerBackoff(SiloAddress peer)
    {
        lock (_failureLock)
        {
            _failureBackoffUntil.Remove(peer);
        }
    }

    private void RecordCompatiblePeer(SiloAddress peer, IEnumerable<string> topics)
    {
        if (Equals(peer, _transport.LocalSilo))
        {
            return;
        }

        lock (_peerCompatibilityLock)
        {
            if (!_confirmedPeerTopics.TryGetValue(peer, out var confirmedTopics))
            {
                confirmedTopics = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
                _confirmedPeerTopics.Add(peer, confirmedTopics);
            }

            var now = _timeProvider.GetUtcNow();
            foreach (var topic in topics)
            {
                confirmedTopics[topic] = now;
            }
        }
    }

    private void SetCompatiblePeerTopics(SiloAddress peer, IEnumerable<string> topics)
    {
        if (Equals(peer, _transport.LocalSilo))
        {
            return;
        }

        lock (_peerCompatibilityLock)
        {
            var confirmedTopics = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            var now = _timeProvider.GetUtcNow();
            foreach (var topic in topics)
            {
                confirmedTopics[topic] = now;
            }

            if (confirmedTopics.Count == 0)
            {
                _confirmedPeerTopics.Remove(peer);
            }
            else
            {
                _confirmedPeerTopics[peer] = confirmedTopics;
            }
        }
    }

    private TimeSpan GetPeerTopicConfirmationTtl(string topicName)
    {
        var antiEntropyInterval = _options.CurrentValue.Overlay.AntiEntropyInterval;
        var expectedUpdateCadence = _topics.TryGetValue(topicName, out var topic)
            ? topic.Options.ExpectedUpdateCadence
            : antiEntropyInterval;
        return antiEntropyInterval >= expectedUpdateCadence
            ? antiEntropyInterval + antiEntropyInterval
            : expectedUpdateCadence + expectedUpdateCadence;
    }

    private IEnumerable<ImmutableDictionary<string, ImmutableArray<DisseminationTopicDigest>>> CreateAntiEntropyRequests(
        Dictionary<string, ImmutableArray<DisseminationTopicDigest>> digestsByTopic)
    {
        var options = _options.CurrentValue;
        var result = new Dictionary<string, ImmutableArray<DisseminationTopicDigest>.Builder>(StringComparer.Ordinal);
        var itemCount = 0;
        var byteCount = 0;
        foreach (var (topic, digests) in digestsByTopic.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            foreach (var digest in digests)
            {
                var digestSize = sizeof(long) + Encoding.UTF8.GetByteCount(topic) + Encoding.UTF8.GetByteCount(digest.Key);
                if (digestSize > options.MaxBatchBytes)
                {
                    continue;
                }

                if (itemCount > 0
                    && (itemCount >= options.MaxBatchItems || byteCount + digestSize > options.MaxBatchBytes))
                {
                    yield return CreateDigestGroups(result);
                    result.Clear();
                    itemCount = 0;
                    byteCount = 0;
                }

                if (!result.TryGetValue(topic, out var topicDigests))
                {
                    topicDigests = ImmutableArray.CreateBuilder<DisseminationTopicDigest>();
                    result.Add(topic, topicDigests);
                }

                topicDigests.Add(digest);
                itemCount++;
                byteCount += digestSize;
            }
        }

        if (itemCount > 0)
        {
            yield return CreateDigestGroups(result);
        }
    }

    private static ImmutableDictionary<string, ImmutableArray<DisseminationTopicDigest>> CreateDigestGroups(
        Dictionary<string, ImmutableArray<DisseminationTopicDigest>.Builder> result) =>
        result.ToImmutableDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutable(),
            StringComparer.Ordinal);

    private static DisseminationAntiEntropyResponse? LimitAntiEntropyResponse(
        DisseminationAntiEntropyResponse response,
        int remainingItems,
        int remainingBytes,
        out bool roundLimitReached)
    {
        var values = new Dictionary<string, ImmutableArray<DisseminationValue>.Builder>(StringComparer.Ordinal);
        var itemCount = 0;
        var byteCount = 0;
        roundLimitReached = false;
        foreach (var (topic, topicValues) in response.ValuesByTopic.OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            foreach (var value in topicValues)
            {
                if (itemCount >= remainingItems || byteCount + value.Payload.Length > remainingBytes)
                {
                    roundLimitReached = true;
                    break;
                }

                AddToValueGroups(values, topic, value);
                itemCount++;
                byteCount += value.Payload.Length;
            }

            if (roundLimitReached)
            {
                break;
            }
        }

        if (itemCount == 0)
        {
            return null;
        }

        return new DisseminationAntiEntropyResponse
        {
            Sender = response.Sender,
            ValuesByTopic = CreateValueGroups(values),
            Truncated = response.Truncated || roundLimitReached,
            SupportedTopics = response.SupportedTopics,
        };
    }

    private void SetPeerBackoff(SiloAddress peer)
    {
        lock (_failureLock)
        {
            _failureBackoffUntil[peer] = _timeProvider.GetUtcNow() + _options.CurrentValue.FailureBackoff;
        }
    }

    private void EmitApplyResult(string topicName, DisseminationValue item, SiloAddress sender, DisseminationApplyResult result)
    {
        DisseminationEvents.EmitValue(topicName, item.Digest, _transport.LocalSilo, sender, result, item.Payload.Length);
        DisseminationInstruments.OnValueApplied(topicName, result);
    }

    private DisseminationAntiEntropyResponse CreateAntiEntropyResponse(
        ImmutableDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic,
        bool truncated) => new()
    {
        Sender = _transport.LocalSilo,
        ValuesByTopic = valuesByTopic,
        Truncated = truncated,
        SupportedTopics = _options.CurrentValue.Enabled
            ? [.. _topics.Values.Where(static topic => topic.IsEnabled).Select(static topic => topic.Name).Order(StringComparer.Ordinal)]
            : [],
    };

    private static int GetDigestCount(ImmutableDictionary<string, ImmutableArray<DisseminationTopicDigest>> digestsByTopic)
    {
        var result = 0;
        foreach (var digests in digestsByTopic.Values)
        {
            result += digests.Length;
        }

        return result;
    }

    private static int GetValueCount(ImmutableDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic)
    {
        var result = 0;
        foreach (var values in valuesByTopic.Values)
        {
            result += values.Length;
        }

        return result;
    }

    private static int GetValueByteCount(ImmutableDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic)
    {
        var result = 0;
        foreach (var values in valuesByTopic.Values)
        {
            foreach (var value in values)
            {
                result += value.Payload.Length;
            }
        }

        return result;
    }

    private static ImmutableDictionary<string, ImmutableArray<DisseminationValue>> GroupValuesByTopic(IReadOnlyList<TopicValue> values)
    {
        var result = new Dictionary<string, ImmutableArray<DisseminationValue>.Builder>(StringComparer.Ordinal);
        foreach (var (topic, value) in values)
        {
            AddToValueGroups(result, topic, value);
        }

        return CreateValueGroups(result);
    }

    private static void AddToValueGroups(
        Dictionary<string, ImmutableArray<DisseminationValue>.Builder> result,
        string topic,
        DisseminationValue value)
    {
        if (!result.TryGetValue(topic, out var topicValues))
        {
            topicValues = ImmutableArray.CreateBuilder<DisseminationValue>();
            result.Add(topic, topicValues);
        }

        topicValues.Add(value);
    }

    private static ImmutableDictionary<string, ImmutableArray<DisseminationValue>> CreateValueGroups(
        Dictionary<string, ImmutableArray<DisseminationValue>.Builder> result) =>
        result.ToImmutableDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToImmutable(),
            StringComparer.Ordinal);

    public sealed record AntiEntropyState(FrozenDictionary<string, AntiEntropyTopicState> Topics)
    {
        public static readonly AntiEntropyState Empty = new(FrozenDictionary<string, AntiEntropyTopicState>.Empty);
    }

    public readonly record struct AntiEntropyTopicState(
        ImmutableArray<SiloAddress> Peers,
        ImmutableArray<DisseminationTopicDigest> Digests);

    private readonly record struct DigestKey(string Topic, string Key);

    private readonly record struct TopicValue(string Topic, DisseminationValue Value);

    private readonly record struct PendingTopicValues(string Topic, ImmutableArray<DisseminationValue> Values);

    private sealed class PendingGossipBatch(DateTimeOffset flushAfter)
    {
        private readonly Dictionary<string, Dictionary<string, DisseminationValue>> _valuesByTopic = new(StringComparer.Ordinal);

        public DateTimeOffset FlushAfter { get; set; } = flushAfter;

        public int Count { get; private set; }

        public int ByteCount { get; private set; }

        public int GetTopicCount(string topic) => _valuesByTopic.TryGetValue(topic, out var values) ? values.Count : 0;

        public bool TryGetValue(DigestKey key, out DisseminationValue value)
        {
            if (_valuesByTopic.TryGetValue(key.Topic, out var topicValues))
            {
                return topicValues.TryGetValue(key.Key, out value!);
            }

            value = default!;
            return false;
        }

        public bool TryAddOrReplace(
            DigestKey key,
            DisseminationValue value,
            int maxTopicItems,
            int maxItems,
            int maxBytes)
        {
            if (!_valuesByTopic.TryGetValue(key.Topic, out var topicValues))
            {
                topicValues = new Dictionary<string, DisseminationValue>(StringComparer.Ordinal);
            }

            if (topicValues.TryGetValue(key.Key, out var previous))
            {
                if (ByteCount - previous.Payload.Length + value.Payload.Length > maxBytes)
                {
                    return false;
                }

                ByteCount -= previous.Payload.Length;
            }
            else
            {
                if (topicValues.Count >= maxTopicItems
                    || Count >= maxItems
                    || ByteCount + value.Payload.Length > maxBytes)
                {
                    return false;
                }

                if (topicValues.Count == 0)
                {
                    _valuesByTopic.Add(key.Topic, topicValues);
                }

                Count++;
            }

            topicValues[key.Key] = value;
            ByteCount += value.Payload.Length;
            return true;
        }

        public ImmutableArray<PendingTopicValues> ToImmutableValuesByTopic()
        {
            var result = ImmutableArray.CreateBuilder<PendingTopicValues>(_valuesByTopic.Count);
            foreach (var (topic, values) in _valuesByTopic)
            {
                result.Add(new PendingTopicValues(topic, [.. values.Values]));
            }

            return result.ToImmutable();
        }
    }

    private sealed record ParticipantTopology(
        ImmutableArray<SiloAddress> Participants,
        FrozenDictionary<SiloAddress, int> Indices)
    {
        public static readonly ParticipantTopology Empty = new(
            [],
            FrozenDictionary<SiloAddress, int>.Empty);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination send to {Peer} failed.")]
    private static partial void LogDebugDisseminationSendFailed(ILogger logger, Exception exception, SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to apply anti-entropy repair value from {Sender} for topic {Topic}, key {Key}, version {Version}.")]
    private static partial void LogDebugAntiEntropyRepairValueFailed(ILogger logger, Exception exception, SiloAddress sender, string topic, string key, long version);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination gossip batch flush failed.")]
    private static partial void LogDebugGossipFlushFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination root {Root} is missing from local membership; refreshing membership before routing.")]
    private static partial void LogDebugDisseminationRootMissing(ILogger logger, SiloAddress root);
}

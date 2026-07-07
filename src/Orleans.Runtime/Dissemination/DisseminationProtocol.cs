using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationProtocol(
    IDisseminationTransport transport,
    DisseminationMembership membership,
    IOptionsMonitor<DisseminationOptions> options,
    IEnumerable<IDisseminationTopic> topics,
    TimeProvider timeProvider,
    ILogger<DisseminationProtocol> logger)
{
    private readonly IDisseminationTransport _transport = transport;
    private readonly DisseminationMembership _membership = membership;
    private readonly IOptionsMonitor<DisseminationOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger = logger;
    private readonly Dictionary<SiloAddress, DateTimeOffset> _failureBackoffUntil = [];
    private readonly object _failureLock = new();
    private readonly object _gossipQueueLock = new();
    private readonly object _recentUpdateLock = new();
    private readonly Dictionary<SiloAddress, PendingGossipBatch> _pendingGossip = [];
    private readonly Dictionary<DigestKey, DateTimeOffset> _lastUpdateReceivedAt = [];
    private readonly FrozenDictionary<string, IDisseminationTopic> _topics = topics.ToFrozenDictionary(static topic => topic.Name, StringComparer.Ordinal);
    private DateTimeOffset? _nextGossipFlushAt;
    private CancellationTokenSource? _gossipFlushWakeup;
    private bool _gossipFlushScheduled;

    public async ValueTask<bool> Publish(
        string topicName,
        DisseminationValue item,
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
        Debug.Assert(Equals(root, _transport.LocalSilo), "Published dissemination values should originate from the local silo.");
        var membership = await GetMembershipSnapshotForRouting(topic.MembershipScope, root, cancellationToken);
        if (membership is null)
        {
            return false;
        }

        var treeTargets = membership.GetOriginatorTreeTargets(topic.MembershipScope, root, GetFanOutFactor);
        RecordRecentUpdate(topic.Name, item.Digest);
        foreach (var peer in treeTargets)
        {
            EnqueueGossip(peer, item, topic);
        }

        return true;
    }

    public async Task ReceiveGossip(DisseminationGossipBatch batch, CancellationToken cancellationToken)
    {
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
        var membership = _membership.CurrentSnapshot;
        PrunePeerState(membership);
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
                SelectAntiEntropyPeers(membership, topic.MembershipScope),
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
        foreach (var (peer, pendingRequest) in requestsByPeer)
        {
            var request = new DisseminationAntiEntropyRequest
            {
                Sender = _transport.LocalSilo,
                DigestsByTopic = pendingRequest.ToFrozenDictionary(StringComparer.Ordinal),
            };

            var response = await SafeRequest(
                peer,
                cancellationToken,
                target => _transport.ExchangeAntiEntropy(target, request, cancellationToken));
            if (response is null)
            {
                continue;
            }

            DisseminationInstruments.OnAntiEntropyExchange(
                "out",
                GetDigestCount(request.DigestsByTopic),
                GetValueCount(response.ValuesByTopic),
                response.Truncated);
            responses.Add(response);
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
        if (!_options.CurrentValue.Enabled)
        {
            return CreateAntiEntropyResponse(FrozenDictionary<string, ImmutableArray<DisseminationValue>>.Empty, truncated: false);
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
        var membership = await GetMembershipSnapshotForRouting(topic.MembershipScope, root, cancellationToken);
        if (membership is null)
        {
            return;
        }

        foreach (var peer in membership.GetForwardingTreeTargets(topic.MembershipScope, _transport.LocalSilo, root, sender, GetFanOutFactor))
        {
            EnqueueGossip(peer, item, topic);
        }
    }

    internal async Task FlushPendingGossip(CancellationToken cancellationToken)
    {
        var batches = DrainPendingGossip(force: true);
        CancelScheduledGossipFlushDelay();
        await SendGossipBatches(batches, cancellationToken);
    }

    private void EnqueueGossip(SiloAddress peer, DisseminationValue item, IDisseminationTopic topic)
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
                return;
            }
            else if (now + topic.Options.MaxCoalescingDelay < pending.FlushAfter)
            {
                pending.FlushAfter = now + topic.Options.MaxCoalescingDelay;
            }

            pending.AddOrReplace(key, item);
            if (pending.Count >= _options.CurrentValue.MaxBatchItems
                || pending.ByteCount >= _options.CurrentValue.MaxBatchBytes
                || pending.GetTopicCount(topic.Name) >= topic.Options.MaxPendingItemCount)
            {
                pending.FlushAfter = now;
            }
        }

        ScheduleGossipFlush();
    }

    private void ScheduleGossipFlush()
    {
        var startFlushLoop = false;
        lock (_gossipQueueLock)
        {
            if (_pendingGossip.Count == 0)
            {
                return;
            }

            var next = GetNextPendingGossipFlushUnsafe();
            if (!_gossipFlushScheduled)
            {
                _gossipFlushScheduled = true;
                _nextGossipFlushAt = next;
                startFlushLoop = true;
            }
            else if (_nextGossipFlushAt is null || next < _nextGossipFlushAt.Value)
            {
                _nextGossipFlushAt = next;
                _gossipFlushWakeup?.Cancel();
            }
        }

        if (startFlushLoop)
        {
            _ = Task.Run(RunScheduledGossipFlush);
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
                        continue;
                    }
                }

                var batches = DrainPendingGossip(force: false);
                await SendGossipBatches(batches, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            LogDebugGossipFlushFailed(_logger, exception);
        }
        finally
        {
            bool reschedule;
            lock (_gossipQueueLock)
            {
                _gossipFlushScheduled = false;
                _nextGossipFlushAt = null;
                DisposeGossipFlushWakeupUnsafe();
                reschedule = _pendingGossip.Count > 0;
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
            if (_pendingGossip.Count == 0)
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
            _gossipFlushWakeup = new CancellationTokenSource();
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
            List<SiloAddress>? drainedPeers = null;
            foreach (var (peer, pending) in _pendingGossip)
            {
                if (!force && pending.FlushAfter > now)
                {
                    continue;
                }

                (drainedPeers ??= []).Add(peer);
                result.Add((peer, pending.ToImmutableValuesByTopic()));
            }

            if (drainedPeers is not null)
            {
                foreach (var peer in drainedPeers)
                {
                    _pendingGossip.Remove(peer);
                }
            }
        }

        result.Sort(static (left, right) => left.Peer.CompareTo(right.Peer));
        return result;
    }

    private async Task SendGossipBatches(List<(SiloAddress Peer, ImmutableArray<PendingTopicValues> ValuesByTopic)> batches, CancellationToken cancellationToken)
    {
        foreach (var queued in batches)
        {
            await SendGossipBatch(queued.Peer, queued.ValuesByTopic, cancellationToken);
        }
    }

    private async Task SendGossipBatch(SiloAddress peer, IReadOnlyList<PendingTopicValues> valuesByTopic, CancellationToken cancellationToken)
    {
        var currentBatch = new Dictionary<string, ImmutableArray<DisseminationValue>.Builder>(StringComparer.Ordinal);
        var itemCount = 0;
        var byteCount = 0;
        foreach (var group in valuesByTopic)
        {
            if (!TryGetEnabledTopic(group.Topic, out _))
            {
                continue;
            }

            foreach (var item in group.Values)
            {
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
        FrozenDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic,
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
            List<DigestKey>? removedKeys = null;
            foreach (var key in _lastUpdateReceivedAt.Keys)
            {
                if (!currentValueStreams.Contains(key))
                {
                    (removedKeys ??= []).Add(key);
                }
            }

            if (removedKeys is not null)
            {
                foreach (var key in removedKeys)
                {
                    _lastUpdateReceivedAt.Remove(key);
                }
            }
        }
    }

    private ImmutableArray<SiloAddress> SelectAntiEntropyPeers(
        DisseminationMembershipSnapshot membership,
        DisseminationMembershipScope membershipScope)
    {
        var options = _options.CurrentValue.Overlay;
        return membership.SelectAntiEntropyPeers(
            membershipScope,
            _transport.LocalSilo,
            options.AntiEntropyPeerCount);
    }

    private int GetFanOutFactor(int participantCount)
    {
        var overlay = _options.CurrentValue.Overlay;
        return overlay.FanOutFactor?.Invoke(participantCount) ?? GetConfiguredFanOutFactor(overlay, participantCount);
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

    private void PrunePeerState(DisseminationMembershipSnapshot membershipSnapshot)
    {
        var now = _timeProvider.GetUtcNow();

        lock (_failureLock)
        {
            List<SiloAddress>? removedPeers = null;
            foreach (var (peer, until) in _failureBackoffUntil)
            {
                if (_transport.LocalSilo.Equals(peer))
                {
                    continue;
                }

                if (until <= now || !membershipSnapshot.ContainsMember(peer))
                {
                    (removedPeers ??= []).Add(peer);
                }
            }

            if (removedPeers is not null)
            {
                foreach (var peer in removedPeers)
                {
                    _failureBackoffUntil.Remove(peer);
                }
            }
        }

        lock (_gossipQueueLock)
        {
            List<SiloAddress>? removedPeers = null;
            foreach (var peer in _pendingGossip.Keys)
            {
                if (_transport.LocalSilo.Equals(peer))
                {
                    continue;
                }

                if (!membershipSnapshot.ContainsMember(peer))
                {
                    (removedPeers ??= []).Add(peer);
                }
            }

            if (removedPeers is not null)
            {
                foreach (var peer in removedPeers)
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
    }

    private void DisposeGossipFlushWakeupUnsafe()
    {
        _gossipFlushWakeup?.Dispose();
        _gossipFlushWakeup = null;
    }

    private async ValueTask<DisseminationMembershipSnapshot?> GetMembershipSnapshotForRouting(
        DisseminationMembershipScope membershipScope,
        SiloAddress root,
        CancellationToken cancellationToken)
    {
        var membership = _membership.CurrentSnapshot;
        if (membership.ContainsParticipant(membershipScope, root))
        {
            PrunePeerState(membership);
            return membership;
        }

        LogDebugDisseminationRootMissing(_logger, root);
        await _membership.RefreshMembership(cancellationToken);
        membership = _membership.CurrentSnapshot;
        PrunePeerState(membership);
        return membership.ContainsParticipant(membershipScope, root) ? membership : null;
    }

    private bool TryGetEnabledTopic(string topicName, [NotNullWhen(true)] out IDisseminationTopic? topic)
    {
        if (_options.CurrentValue.Enabled
            && _topics.TryGetValue(topicName, out topic)
            && topic.IsEnabled)
        {
            return true;
        }

        topic = null;
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
        FrozenDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic,
        bool truncated) => new()
    {
        Sender = _transport.LocalSilo,
        ValuesByTopic = valuesByTopic,
        Truncated = truncated,
    };

    private static int GetDigestCount(FrozenDictionary<string, ImmutableArray<DisseminationTopicDigest>> digestsByTopic)
    {
        var result = 0;
        foreach (var digests in digestsByTopic.Values)
        {
            result += digests.Length;
        }

        return result;
    }

    private static int GetValueCount(FrozenDictionary<string, ImmutableArray<DisseminationValue>> valuesByTopic)
    {
        var result = 0;
        foreach (var values in valuesByTopic.Values)
        {
            result += values.Length;
        }

        return result;
    }

    private static FrozenDictionary<string, ImmutableArray<DisseminationValue>> GroupValuesByTopic(IReadOnlyList<TopicValue> values)
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

    private static FrozenDictionary<string, ImmutableArray<DisseminationValue>> CreateValueGroups(
        Dictionary<string, ImmutableArray<DisseminationValue>.Builder> result) =>
        result.ToFrozenDictionary(
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

        public bool TryGetValue(DigestKey key, [NotNullWhen(true)] out DisseminationValue? value)
        {
            if (_valuesByTopic.TryGetValue(key.Topic, out var topicValues))
            {
                return topicValues.TryGetValue(key.Key, out value!);
            }

            value = null;
            return false;
        }

        public void AddOrReplace(DigestKey key, DisseminationValue value)
        {
            if (!_valuesByTopic.TryGetValue(key.Topic, out var topicValues))
            {
                topicValues = new Dictionary<string, DisseminationValue>(StringComparer.Ordinal);
                _valuesByTopic.Add(key.Topic, topicValues);
            }

            if (topicValues.TryGetValue(key.Key, out var previous))
            {
                ByteCount -= previous.Payload.Length;
            }
            else
            {
                Count++;
            }

            topicValues[key.Key] = value;
            ByteCount += value.Payload.Length;
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

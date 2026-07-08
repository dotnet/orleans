using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationProtocol
{
    private readonly IDisseminationTransport _transport;
    private readonly DisseminationMembership _membership;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger;
    private readonly DisseminationGossipQueue _gossipQueue;
    private readonly Dictionary<SiloAddress, DateTimeOffset> _failureBackoffUntil = [];
    private readonly object _failureLock = new();
    private readonly object _seenValueLock = new();
    private readonly Dictionary<DigestKey, DateTimeOffset> _lastValueSeenAt = [];
    private readonly FrozenDictionary<string, IDisseminationTopic> _topics;

    public DisseminationProtocol(
        IDisseminationTransport transport,
        DisseminationMembership membership,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationTopic> topics,
        TimeProvider timeProvider,
        ILogger<DisseminationProtocol> logger)
    {
        _transport = transport;
        _membership = membership;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _topics = topics.ToFrozenDictionary(static topic => topic.Name, StringComparer.Ordinal);
        _gossipQueue = new DisseminationGossipQueue(
            timeProvider,
            SendGossipBatches,
            exception => LogDebugGossipFlushFailed(_logger, exception));
    }

    public async ValueTask<bool> Publish(
        IDisseminationTopic topic,
        DisseminationValue item,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled || !topic.IsEnabled)
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
        RecordSeenValue(topic.Name, item.Digest);
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
                await ApplyReceivedValue(topic, item, batch.Sender, forward: true, cancellationToken);
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
        var membership = _membership.CurrentSnapshot;
        PrunePeerState(membership);
        var peersByScope = new Dictionary<DisseminationMembershipScope, ImmutableArray<SiloAddress>>();
        ImmutableArray<SiloAddress> GetAntiEntropyPeers(DisseminationMembershipScope membershipScope)
        {
            if (!peersByScope.TryGetValue(membershipScope, out var peers))
            {
                peers = SelectAntiEntropyPeers(membership, membershipScope);
                peersByScope.Add(membershipScope, peers);
            }

            return peers;
        }

        var currentValueStreams = new HashSet<DigestKey>();
        var now = _timeProvider.GetUtcNow();
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
                GetAntiEntropyPeers(topic.MembershipScope),
                [.. topicDigests]));
        }

        PruneSeenValues(currentValueStreams);
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

            var response = await ExecutePeerOperation<DisseminationAntiEntropyResponse?>(
                peer,
                cancellationToken,
                async target => await _transport.ExchangeAntiEntropy(target, request, cancellationToken),
                failureResult: null);
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
                        await ApplyReceivedValue(topic, item, response.Sender, forward: false, cancellationToken);
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
                    || !ValidatePayloadSize(requestedTopic, item))
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
        IDisseminationTopic topic,
        DisseminationValue value,
        SiloAddress sender,
        bool forward,
        CancellationToken cancellationToken)
    {
        var topicName = topic.Name;
        if (!ValidatePayloadSize(topic, value))
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
            RecordSeenValue(topicName, value.Digest);
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
        await _gossipQueue.FlushPendingGossip(cancellationToken);
    }

    private void EnqueueGossip(SiloAddress peer, DisseminationValue item, IDisseminationTopic topic)
    {
        _gossipQueue.Enqueue(
            peer,
            item,
            topic,
            _options.CurrentValue.MaxBatchItems,
            _options.CurrentValue.MaxBatchBytes);
    }

    private async Task SendGossipBatches(IReadOnlyList<DisseminationGossipQueue.Batch> batches, CancellationToken cancellationToken)
    {
        foreach (var queued in batches)
        {
            await SendGossipBatch(queued.Peer, queued.ValuesByTopic, cancellationToken);
        }
    }

    private async Task SendGossipBatch(SiloAddress peer, IReadOnlyList<DisseminationGossipQueue.PendingTopicValues> valuesByTopic, CancellationToken cancellationToken)
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

        var sent = await ExecutePeerOperation(
            peer,
            cancellationToken,
            async target =>
            {
                await _transport.SendGossip(target, batch, cancellationToken);
                return true;
            },
            failureResult: false);
        if (sent)
        {
            DisseminationInstruments.OnGossipSent(batch.ValuesByTopic, "tree");
        }
    }

    private async ValueTask<T> ExecutePeerOperation<T>(
        SiloAddress peer,
        CancellationToken cancellationToken,
        Func<SiloAddress, ValueTask<T>> operation,
        T failureResult)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsPeerBackedOff(peer))
        {
            return failureResult;
        }

        try
        {
            var result = await operation(peer);
            ClearPeerBackoff(peer);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogDebugDisseminationSendFailed(_logger, exception, peer);
            SetPeerBackoff(peer);
            return failureResult;
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
        lock (_seenValueLock)
        {
            return !_lastValueSeenAt.TryGetValue(key, out var lastSeen)
                || now - lastSeen >= topic.Options.ExpectedUpdateCadence;
        }
    }

    private void RecordSeenValue(string topicName, DisseminationTopicDigest digest)
    {
        var key = new DigestKey(topicName, digest.Key);
        lock (_seenValueLock)
        {
            _lastValueSeenAt[key] = _timeProvider.GetUtcNow();
        }
    }

    private void PruneSeenValues(HashSet<DigestKey> currentValueStreams)
    {
        lock (_seenValueLock)
        {
            List<DigestKey>? removedKeys = null;
            foreach (var key in _lastValueSeenAt.Keys)
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
                    _lastValueSeenAt.Remove(key);
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

        _gossipQueue.Prune(membershipSnapshot, _transport.LocalSilo);
    }

    private async ValueTask<DisseminationMembershipSnapshot?> GetMembershipSnapshotForRouting(
        DisseminationMembershipScope membershipScope,
        SiloAddress root,
        CancellationToken cancellationToken)
    {
        if (!_membership.CurrentSnapshot.ContainsParticipant(membershipScope, root))
        {
            LogDebugDisseminationRootMissing(_logger, root);
        }

        var membership = await _membership.GetSnapshotContainingParticipant(membershipScope, root, cancellationToken);
        PrunePeerState(membership ?? _membership.CurrentSnapshot);
        return membership;
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

    private bool ValidatePayloadSize(IDisseminationTopic topic, DisseminationValue item)
    {
        var options = _options.CurrentValue;
        if (item.Payload.Length > topic.Options.MaxPayloadBytes || item.Payload.Length > options.MaxBatchBytes)
        {
            var topicName = topic.Name;
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

        return ValidatePayloadSize(topic, item) ? null : "oversize";
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

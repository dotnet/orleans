using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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
    ILogger<DisseminationProtocol> logger)
{
    private readonly IDisseminationTransport _transport = transport;
    private readonly IOptionsMonitor<DisseminationOptions> _options = options;
    private readonly FrozenDictionary<string, IDisseminationTopic> _topics = topics.ToFrozenDictionary(static topic => topic.Name, StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger = logger;
    private readonly Dictionary<SiloAddress, DateTimeOffset> _failureBackoffUntil = [];
    private readonly object _failureLock = new();
    private readonly object _gossipQueueLock = new();
    private readonly object _recentUpdateLock = new();
    private readonly object _topologyLock = new();
    private readonly Dictionary<SiloAddress, PendingGossipBatch> _pendingGossip = [];
    private readonly Dictionary<DigestKey, DateTimeOffset> _lastUpdateReceivedAt = [];
    private DateTimeOffset? _nextGossipFlushAt;
    private CancellationTokenSource? _gossipFlushWakeup;
    private bool _gossipFlushScheduled;
    private ParticipantTopology _activeMembersTopology = ParticipantTopology.Empty;
    private ParticipantTopology _allMembersTopology = ParticipantTopology.Empty;
    private long _antiEntropyRound;

    public FrozenDictionary<string, IDisseminationTopic> Topics => _topics;

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
            DisseminationInstruments.OnFallback(item.Digest.Topic, reason);
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

        RecordRecentUpdate(item.Digest);
        foreach (var peer in GetOriginatorTreeTargets(topology, root))
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

        DisseminationInstruments.OnGossipReceived(batch.Values, "tree");

        foreach (var item in batch.Values)
        {
            await ApplyReceivedValue(item, batch.Sender, forward: true, cancellationToken);
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

        var requestsByPeer = new Dictionary<SiloAddress, (List<string> Topics, List<DisseminationDigest> Digests)>();
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
                    pendingRequest = ([], []);
                    requestsByPeer.Add(peer, pendingRequest);
                }

                pendingRequest.Topics.Add(topicName);
                foreach (var digest in topicState.Digests)
                {
                    pendingRequest.Digests.Add(new DisseminationDigest(topicName, digest.Key, digest.Version));
                }
            }
        }

        var responses = new List<DisseminationAntiEntropyResponse>(requestsByPeer.Count);
        foreach (var (peer, pendingRequest) in requestsByPeer)
        {
            var request = new DisseminationAntiEntropyRequest
            {
                Sender = _transport.LocalSilo,
                Topics = [.. pendingRequest.Topics],
                Digests = [.. pendingRequest.Digests],
            };

            var response = await SafeRequest(peer, target => _transport.ExchangeAntiEntropy(target, request, cancellationToken));
            if (response is null)
            {
                continue;
            }

            DisseminationInstruments.OnAntiEntropyExchange("out", request.Digests.Length, response.Values.Length, response.Truncated);
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
            foreach (var item in response.Values)
            {
                try
                {
                    await ApplyReceivedValue(item, response.Sender, forward: false, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogDebugAntiEntropyRepairValueFailed(_logger, exception, response.Sender, item.Digest.Topic, item.Digest.Key, item.Digest.Version);
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
            return CreateAntiEntropyResponse([], truncated: false);
        }

        var remoteDigests = GetRemoteDigestMap(request.Digests);
        var values = new List<DisseminationValue>();
        var byteCount = 0;
        var truncated = false;
        var options = _options.CurrentValue;

        foreach (var topicName in request.Topics)
        {
            if (!TryGetEnabledTopic(topicName, out var requestedTopic))
            {
                continue;
            }

            foreach (var topicDigest in requestedTopic.GetDigests())
            {
                var digestKey = new DigestKey(requestedTopic.Name, topicDigest.Key);
                if (!remoteDigests.TryGetValue(digestKey, out var remoteDigest))
                {
                    continue;
                }

                var localDigest = new DisseminationDigest(requestedTopic.Name, topicDigest.Key, topicDigest.Version);
                if (requestedTopic.CompareVersion(localDigest, remoteDigest) <= 0)
                {
                    continue;
                }

                var item = await requestedTopic.GetValue(
                    localDigest,
                    remoteDigest,
                    cancellationToken);
                if (item is null
                    || !string.Equals(item.Digest.Topic, requestedTopic.Name, StringComparison.Ordinal)
                    || !ValidatePayloadSize(requestedTopic, item))
                {
                    continue;
                }

                if (values.Count >= options.MaxBatchItems || byteCount + item.Payload.Length > options.MaxBatchBytes)
                {
                    truncated = true;
                    break;
                }

                values.Add(item);
                byteCount += item.Payload.Length;
            }

            if (truncated)
            {
                break;
            }
        }

        DisseminationInstruments.OnAntiEntropyExchange("in", request.Digests.Length, values.Count, truncated);
        return CreateAntiEntropyResponse([.. values], truncated);
    }

    private async ValueTask<DisseminationApplyResult> ApplyReceivedValue(
        DisseminationValue value,
        SiloAddress sender,
        bool forward,
        CancellationToken cancellationToken)
    {
        if (!TryGetEnabledTopic(value.Digest.Topic, out var topic))
        {
            EmitApplyResult(value, sender, DisseminationApplyResult.Rejected);
            return DisseminationApplyResult.Rejected;
        }

        if (!ValidatePayloadSize(topic, value))
        {
            return DisseminationApplyResult.Rejected;
        }

        if (IsExpired(value) || topic.IsObsolete(value.Digest))
        {
            EmitApplyResult(value, sender, DisseminationApplyResult.Obsolete);
            return DisseminationApplyResult.Obsolete;
        }

        var result = await topic.ApplyValue(value, cancellationToken);
        EmitApplyResult(value, sender, result);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            RecordRecentUpdate(value.Digest);
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
        var batches = DrainPendingGossip(force: true);
        CancelScheduledGossipFlushDelay();
        await SendGossipBatches(batches, cancellationToken);
    }

    private void EnqueueGossip(SiloAddress peer, DisseminationValue item, IDisseminationTopic topic)
    {
        var now = _timeProvider.GetUtcNow();
        var key = new DigestKey(item.Digest.Topic, item.Digest.Key);
        lock (_gossipQueueLock)
        {
            if (!_pendingGossip.TryGetValue(peer, out var pending))
            {
                pending = new PendingGossipBatch(now + topic.Options.MaxCoalescingDelay);
                _pendingGossip.Add(peer, pending);
            }
            else if (pending.Values.TryGetValue(key, out var existing)
                && topic.CompareVersion(existing.Digest, item.Digest) >= 0)
            {
                return;
            }
            else if (now + topic.Options.MaxCoalescingDelay < pending.FlushAfter)
            {
                pending.FlushAfter = now + topic.Options.MaxCoalescingDelay;
            }

            pending.AddOrReplace(key, item);
            if (pending.Values.Count >= _options.CurrentValue.MaxBatchItems
                || pending.ByteCount >= _options.CurrentValue.MaxBatchBytes
                || pending.GetTopicCount(item.Digest.Topic) >= topic.Options.MaxPendingItemCount)
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
            var reschedule = false;
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

    private List<(SiloAddress Peer, ImmutableArray<DisseminationValue> Values)> DrainPendingGossip(bool force)
    {
        var now = _timeProvider.GetUtcNow();
        var result = new List<(SiloAddress Peer, ImmutableArray<DisseminationValue> Values)>();
        lock (_gossipQueueLock)
        {
            foreach (var (peer, pending) in _pendingGossip)
            {
                if (!force && pending.FlushAfter > now)
                {
                    continue;
                }

                _pendingGossip.Remove(peer);
                result.Add((peer, [.. pending.Values.Values]));
            }
        }

        result.Sort(static (left, right) => left.Peer.CompareTo(right.Peer));
        return result;
    }

    private async Task SendGossipBatches(List<(SiloAddress Peer, ImmutableArray<DisseminationValue> Values)> batches, CancellationToken cancellationToken)
    {
        foreach (var queued in batches)
        {
            await SendGossipBatch(queued.Peer, queued.Values, cancellationToken);
        }
    }

    private async Task SendGossipBatch(SiloAddress peer, IReadOnlyList<DisseminationValue> values, CancellationToken cancellationToken)
    {
        var currentBatch = new List<DisseminationValue>();
        var byteCount = 0;
        foreach (var item in values)
        {
            if (!TryGetEnabledTopic(item.Digest.Topic, out var topic))
            {
                continue;
            }

            if (currentBatch.Count > 0
                && (currentBatch.Count >= _options.CurrentValue.MaxBatchItems
                    || byteCount + item.Payload.Length > _options.CurrentValue.MaxBatchBytes))
            {
                await SendGossipBatchCore(peer, [.. currentBatch], cancellationToken);
                currentBatch.Clear();
                byteCount = 0;
            }

            currentBatch.Add(item);
            byteCount += item.Payload.Length;
        }

        if (currentBatch.Count > 0)
        {
            await SendGossipBatchCore(peer, [.. currentBatch], cancellationToken);
        }
    }

    private async Task SendGossipBatchCore(SiloAddress peer, ImmutableArray<DisseminationValue> values, CancellationToken cancellationToken)
    {
        var batch = new DisseminationGossipBatch
        {
            Sender = _transport.LocalSilo,
            Values = values,
        };

        var sent = await SafeSend(peer, target => _transport.SendGossip(target, batch, cancellationToken));
        if (sent)
        {
            DisseminationInstruments.OnGossipSent(values, "tree");
        }
    }

    private async ValueTask<bool> SafeSend(SiloAddress peer, Func<SiloAddress, Task> send)
    {
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
        catch (Exception exception)
        {
            LogDebugDisseminationSendFailed(_logger, exception, peer);
            SetPeerBackoff(peer);
            return false;
        }
    }

    private async ValueTask<DisseminationAntiEntropyResponse?> SafeRequest(
        SiloAddress peer,
        Func<SiloAddress, ValueTask<DisseminationAntiEntropyResponse>> request)
    {
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
        catch (Exception exception)
        {
            LogDebugDisseminationSendFailed(_logger, exception, peer);
            SetPeerBackoff(peer);
            return null;
        }
    }

    private Dictionary<DigestKey, DisseminationDigest> GetRemoteDigestMap(ImmutableArray<DisseminationDigest> digests)
    {
        var result = new Dictionary<DigestKey, DisseminationDigest>(digests.Length);
        foreach (var digest in digests)
        {
            if (!TryGetEnabledTopic(digest.Topic, out var topic))
            {
                continue;
            }

            var key = new DigestKey(digest.Topic, digest.Key);
            if (!result.TryGetValue(key, out var existing) || topic.CompareVersion(digest, existing) > 0)
            {
                result[key] = digest;
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

    private void RecordRecentUpdate(DisseminationDigest digest)
    {
        var key = new DigestKey(digest.Topic, digest.Key);
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

    private bool ValidatePayloadSize(IDisseminationTopic topic, DisseminationValue item)
    {
        var options = _options.CurrentValue;
        if (item.Payload.Length > topic.Options.MaxPayloadBytes || item.Payload.Length > options.MaxBatchBytes)
        {
            DisseminationEvents.EmitPayloadDrop(item.Digest, _transport.LocalSilo, "oversize", item.Payload.Length);
            DisseminationInstruments.OnPayloadDropped(item.Digest.Topic, "oversize");
            return false;
        }

        return true;
    }

    private string? GetPublishValidationFailureReason(IDisseminationTopic topic, DisseminationValue item)
    {
        if (!string.Equals(item.Digest.Topic, topic.Name, StringComparison.Ordinal))
        {
            return "topic";
        }

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

    private void EmitApplyResult(DisseminationValue item, SiloAddress sender, DisseminationApplyResult result)
    {
        DisseminationEvents.EmitValue(item.Digest, _transport.LocalSilo, sender, result, item.Payload.Length);
        DisseminationInstruments.OnValueApplied(item.Digest.Topic, result);
    }

    private DisseminationAntiEntropyResponse CreateAntiEntropyResponse(ImmutableArray<DisseminationValue> values, bool truncated) => new()
    {
        Sender = _transport.LocalSilo,
        Values = values,
        Truncated = truncated,
    };

    public sealed record AntiEntropyState(FrozenDictionary<string, AntiEntropyTopicState> Topics)
    {
        public static readonly AntiEntropyState Empty = new(FrozenDictionary<string, AntiEntropyTopicState>.Empty);
    }

    public readonly record struct AntiEntropyTopicState(
        ImmutableArray<SiloAddress> Peers,
        ImmutableArray<DisseminationTopicDigest> Digests);

    private readonly record struct DigestKey(string Topic, string Key);

    private sealed class PendingGossipBatch(DateTimeOffset flushAfter)
    {
        private readonly Dictionary<string, int> _topicCounts = new(StringComparer.Ordinal);

        public Dictionary<DigestKey, DisseminationValue> Values { get; } = [];

        public DateTimeOffset FlushAfter { get; set; } = flushAfter;

        public int ByteCount { get; private set; }

        public int GetTopicCount(string topic) => _topicCounts.GetValueOrDefault(topic);

        public void AddOrReplace(DigestKey key, DisseminationValue value)
        {
            if (Values.TryGetValue(key, out var previous))
            {
                ByteCount -= previous.Payload.Length;
            }
            else
            {
                _topicCounts[key.Topic] = GetTopicCount(key.Topic) + 1;
            }

            Values[key] = value;
            ByteCount += value.Payload.Length;
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

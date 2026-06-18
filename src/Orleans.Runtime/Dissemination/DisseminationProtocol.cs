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
    private readonly Dictionary<CapabilityKey, CapabilityEntry> _capabilityCache = [];
    private readonly Dictionary<CapabilityKey, DateTimeOffset> _capabilityProbeBackoffUntil = [];
    private readonly Dictionary<SiloAddress, DateTimeOffset> _failureBackoffUntil = [];
    private readonly object _capabilityLock = new();
    private readonly object _failureLock = new();
    private readonly object _topologyLock = new();
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

        if (!ValidatePayloadSize(topic, item))
        {
            await topic.OnFallbackRequired(default!, item.Digest, cancellationToken);
            DisseminationInstruments.OnFallback(item.Digest.Topic, "oversize");
            return false;
        }

        var root = item.Root is not null ? item.Root : _transport.LocalSilo;
        item = EnsureRoot(item, root);
        var topology = targetPeers is { Count: > 0 }
            ? BuildParticipantTopology(targetPeers, root, includeLocal: true)
            : GetParticipantTopology(topic.MembershipScope, root, includeLocal: true);
        var activeMembers = targetPeers is { Count: > 0 }
            ? topology.ParticipantSet
            : GetActiveMemberSet(root, includeLocal: true);
        if (!await AreParticipantsCapable(topology.Participants, activeMembers, topic, item.Digest.PayloadKind, cancellationToken))
        {
            return false;
        }

        foreach (var peer in GetOriginatorTreeTargets(topology, root))
        {
            await SendGossip(peer, item, cancellationToken);
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

        var topics = new List<DisseminationCapabilityRequest>();
        var digests = new List<DisseminationDigest>();
        foreach (var topic in _topics.Values)
        {
            if (!topic.IsEnabled)
            {
                continue;
            }

            topics.Add(new DisseminationCapabilityRequest
            {
                Topic = topic.Name,
                ProtocolVersion = topic.ProtocolVersion,
                PayloadKinds = [.. topic.PayloadKinds],
            });

            foreach (var digest in topic.GetDigests())
            {
                if (string.Equals(digest.Topic, topic.Name, StringComparison.Ordinal)
                    && topic.PayloadKinds.Contains(digest.PayloadKind))
                {
                    digests.Add(digest);
                }
            }
        }

        var round = Interlocked.Increment(ref _antiEntropyRound);
        return new AntiEntropyState(
            GetAntiEntropyPeersByTopic(topics, round),
            topics.ToArray(),
            SortDigests(digests).ToArray());
    }

    public async Task<IReadOnlyList<DisseminationAntiEntropyResponse>> ExchangeAntiEntropy(
        AntiEntropyState state,
        CancellationToken cancellationToken)
    {
        if (state.PeersByTopic.Count == 0 || state.Topics.Count == 0)
        {
            return [];
        }

        var peers = state.PeersByTopic.Values.SelectMany(static value => value).Distinct().ToArray();
        var responses = new List<DisseminationAntiEntropyResponse>(peers.Length);
        foreach (var peer in peers)
        {
            var requestedTopics = state.Topics
                .Where(request => state.PeersByTopic.TryGetValue(request.Topic, out var topicPeers) && topicPeers.Contains(peer))
                .ToArray();
            var topics = await GetCapableAntiEntropyTopics(peer, requestedTopics, cancellationToken);
            if (topics.Count == 0)
            {
                continue;
            }

            var digests = state.Digests.Where(digest => IsRequested(topics, digest)).ToArray();
            var request = new DisseminationAntiEntropyRequest
            {
                Sender = _transport.LocalSilo,
                Topics = [.. topics],
                Digests = digests,
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

        var requestedTopics = GetRequestedTopics(request);
        if (requestedTopics.Count == 0)
        {
            return CreateAntiEntropyResponse([], truncated: false);
        }

        var remoteDigests = GetRemoteDigestMap(request.Digests);
        var values = new List<DisseminationValue>();
        var byteCount = 0;
        var truncated = false;
        var options = _options.CurrentValue;

        foreach (var requestedTopic in requestedTopics.Values.OrderBy(static topic => topic.Topic.Name, StringComparer.Ordinal))
        {
            foreach (var localDigest in SortDigests(requestedTopic.Topic.GetDigests()))
            {
                if (!requestedTopic.PayloadKinds.Contains(localDigest.PayloadKind))
                {
                    continue;
                }

                var digestKey = GetDigestKey(localDigest);
                var hasRemoteDigest = remoteDigests.TryGetValue(digestKey, out var remoteDigest);
                if (hasRemoteDigest && requestedTopic.Topic.CompareVersion(localDigest, remoteDigest) <= 0)
                {
                    continue;
                }

                var item = await requestedTopic.Topic.GetValue(
                    localDigest,
                    hasRemoteDigest ? remoteDigest : null,
                    requestedTopic.PayloadKinds,
                    cancellationToken);
                if (item is null
                    || !requestedTopic.PayloadKinds.Contains(item.Digest.PayloadKind)
                    || !ValidatePayloadSize(requestedTopic.Topic, item))
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

        if (!topic.PayloadKinds.Contains(value.Digest.PayloadKind))
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

        if (result == DisseminationApplyResult.Applied && forward)
        {
            await Forward(value, topic, sender, cancellationToken);
        }

        return result;
    }

    private async Task Forward(DisseminationValue item, IDisseminationTopic topic, SiloAddress sender, CancellationToken cancellationToken)
    {
        var root = item.Root is not null ? item.Root : sender;
        item = EnsureRoot(item, root);
        var topology = GetParticipantTopology(topic.MembershipScope, root, includeLocal: true);
        foreach (var peer in GetForwardingTreeTargets(topology, root, sender))
        {
            await SendGossip(peer, item, cancellationToken);
        }
    }

    private async ValueTask<bool> SendGossip(SiloAddress peer, DisseminationValue item, CancellationToken cancellationToken)
    {
        if (!_topics.TryGetValue(item.Digest.Topic, out var topic)
            || await GetCapabilityStatus(peer, topic, item.Digest.PayloadKind, cancellationToken) != CapabilityStatus.Supported)
        {
            return false;
        }

        var batch = new DisseminationGossipBatch
        {
            Sender = _transport.LocalSilo,
            Values = [item],
        };

        var sent = await SafeSend(peer, target => _transport.SendGossip(target, batch, cancellationToken));
        if (sent)
        {
            DisseminationInstruments.OnGossipSent(item.Digest.Topic, "tree", 1, item.Payload.Length);
        }

        return sent;
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

    private async ValueTask<CapabilityStatus> GetCapabilityStatus(
        SiloAddress peer,
        IDisseminationTopic topic,
        string payloadKind,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var key = new CapabilityKey(peer, topic.Name);
        lock (_capabilityLock)
        {
            if (_capabilityCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
            {
                return cached.Supported && cached.PayloadKinds.Contains(payloadKind)
                    ? CapabilityStatus.Supported
                    : CapabilityStatus.Unsupported;
            }

            if (_capabilityProbeBackoffUntil.TryGetValue(key, out var probeBackoffUntil))
            {
                if (probeBackoffUntil > now)
                {
                    return CapabilityStatus.Unavailable;
                }

                _capabilityProbeBackoffUntil.Remove(key);
            }
        }

        var request = new DisseminationCapabilityRequest
        {
            Topic = topic.Name,
            ProtocolVersion = topic.ProtocolVersion,
            PayloadKinds = [.. topic.PayloadKinds],
        };

        try
        {
            var response = await _transport.GetCapabilities(peer, request, cancellationToken);
            var supported = response.Supported && response.ProtocolVersion >= topic.ProtocolVersion;
            var payloadKinds = response.PayloadKinds.ToFrozenSet(StringComparer.Ordinal);
            lock (_capabilityLock)
            {
                _capabilityCache[key] = new CapabilityEntry(supported, payloadKinds, now + _options.CurrentValue.CapabilityCacheTtl);
                _capabilityProbeBackoffUntil.Remove(key);
            }

            DisseminationEvents.EmitCapabilityProbe(_transport.LocalSilo, peer, topic.Name, supported);
            return supported && payloadKinds.Contains(payloadKind)
                ? CapabilityStatus.Supported
                : CapabilityStatus.Unsupported;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogDebugCapabilityProbeFailed(_logger, exception, peer, topic.Name);
            lock (_capabilityLock)
            {
                _capabilityCache.Remove(key);
                _capabilityProbeBackoffUntil[key] = _timeProvider.GetUtcNow() + _options.CurrentValue.FailureBackoff;
            }

            DisseminationEvents.EmitCapabilityProbe(_transport.LocalSilo, peer, topic.Name, supported: false);
            return CapabilityStatus.Unavailable;
        }
    }

    private async ValueTask<bool> AreParticipantsCapable(
        ImmutableArray<SiloAddress> participants,
        FrozenSet<SiloAddress> activeMembers,
        IDisseminationTopic topic,
        string payloadKind,
        CancellationToken cancellationToken)
    {
        var probes = new List<Task<(SiloAddress Participant, CapabilityStatus Status)>>(participants.Length);
        foreach (var participant in participants)
        {
            if (!Equals(participant, _transport.LocalSilo))
            {
                probes.Add(Probe(participant));
            }
        }

        if (probes.Count == 0)
        {
            return true;
        }

        var results = await Task.WhenAll(probes);
        foreach (var (participant, status) in results)
        {
            if (activeMembers.Contains(participant) && status != CapabilityStatus.Supported)
            {
                return false;
            }
        }

        return true;

        async Task<(SiloAddress Participant, CapabilityStatus Status)> Probe(SiloAddress participant) =>
            (participant, await GetCapabilityStatus(participant, topic, payloadKind, cancellationToken));
    }

    private async ValueTask<List<DisseminationCapabilityRequest>> GetCapableAntiEntropyTopics(
        SiloAddress peer,
        IReadOnlyList<DisseminationCapabilityRequest> topics,
        CancellationToken cancellationToken)
    {
        var result = new List<DisseminationCapabilityRequest>(topics.Count);
        foreach (var request in topics)
        {
            if (!_topics.TryGetValue(request.Topic, out var topic) || !topic.IsEnabled)
            {
                continue;
            }

            var supportedPayloadKinds = new List<string>(request.PayloadKinds.Length);
            foreach (var payloadKind in request.PayloadKinds)
            {
                if (await GetCapabilityStatus(peer, topic, payloadKind, cancellationToken) == CapabilityStatus.Supported)
                {
                    supportedPayloadKinds.Add(payloadKind);
                }
            }

            if (supportedPayloadKinds.Count > 0)
            {
                result.Add(new DisseminationCapabilityRequest
                {
                    Topic = request.Topic,
                    ProtocolVersion = request.ProtocolVersion,
                    PayloadKinds = [.. supportedPayloadKinds],
                });
            }
        }

        return result;
    }

    private FrozenDictionary<string, RequestedTopic> GetRequestedTopics(DisseminationAntiEntropyRequest request)
    {
        var result = new Dictionary<string, RequestedTopic>(StringComparer.Ordinal);
        foreach (var requestedTopic in request.Topics)
        {
            if (!TryGetEnabledTopic(requestedTopic.Topic, out var topic)
                || requestedTopic.ProtocolVersion > topic.ProtocolVersion)
            {
                continue;
            }

            var payloadKinds = topic.PayloadKinds
                .Intersect(requestedTopic.PayloadKinds, StringComparer.Ordinal)
                .ToFrozenSet(StringComparer.Ordinal);
            if (payloadKinds.Count > 0)
            {
                result[topic.Name] = new RequestedTopic(topic, payloadKinds);
            }
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private FrozenDictionary<DigestKey, DisseminationDigest> GetRemoteDigestMap(IEnumerable<DisseminationDigest> digests)
    {
        var result = new Dictionary<DigestKey, DisseminationDigest>();
        foreach (var digest in digests)
        {
            if (!TryGetEnabledTopic(digest.Topic, out var topic))
            {
                continue;
            }

            var key = GetDigestKey(digest);
            if (!result.TryGetValue(key, out var existing) || topic.CompareVersion(digest, existing) > 0)
            {
                result[key] = digest;
            }
        }

        return result.ToFrozenDictionary();
    }

    private FrozenDictionary<string, ImmutableArray<SiloAddress>> GetAntiEntropyPeersByTopic(
        IReadOnlyList<DisseminationCapabilityRequest> topics,
        long round)
    {
        var result = new Dictionary<string, ImmutableArray<SiloAddress>>(StringComparer.Ordinal);
        foreach (var request in topics)
        {
            if (_topics.TryGetValue(request.Topic, out var topic) && topic.IsEnabled)
            {
                result[request.Topic] = SelectAntiEntropyPeers(topic.MembershipScope, round);
            }
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private ImmutableArray<SiloAddress> SelectAntiEntropyPeers(DisseminationMembershipScope membershipScope, long round)
    {
        var options = _options.CurrentValue.Overlay;
        var topology = GetParticipantTopology(membershipScope, root: null, includeLocal: true);
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
        var candidates = GetAntiEntropyCandidateIndexes(localIndex, participants.Length, fanout);
        return [.. candidates
            .Where(index => index != localIndex)
            .OrderBy(index => GetRepairPeerScore(participants[index], round, localIndex))
            .ThenBy(index => participants[index])
            .Take(options.AntiEntropyPeerCount)
            .Select(index => participants[index])];
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

    private static ulong GetRepairPeerScore(SiloAddress peer, long round, int localIndex)
    {
        var value = (ulong)(uint)peer.GetConsistentHashCode();
        value ^= (ulong)round * 0x9E3779B97F4A7C15UL;
        value ^= (ulong)(uint)localIndex << 32;
        return Mix(value);
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

    private ParticipantTopology GetParticipantTopology(
        DisseminationMembershipScope membershipScope,
        SiloAddress? root,
        bool includeLocal)
    {
        var membership = _transport.GetMembership();
        var activeMembers = GetCachedParticipantTopology(
            DisseminationMembershipScope.ActiveMembers,
            membership.ActiveMembers,
            membershipScope == DisseminationMembershipScope.ActiveMembers ? root : null,
            includeLocal);
        var allMembers = GetCachedParticipantTopology(
            DisseminationMembershipScope.AllMembers,
            membership.AllMembers,
            membershipScope == DisseminationMembershipScope.AllMembers ? root : null,
            includeLocal);

        return membershipScope == DisseminationMembershipScope.AllMembers ? allMembers : activeMembers;
    }

    private FrozenSet<SiloAddress> GetActiveMemberSet(SiloAddress? root, bool includeLocal) =>
        GetCachedParticipantTopology(
            DisseminationMembershipScope.ActiveMembers,
            _transport.GetMembership().ActiveMembers,
            root,
            includeLocal).ParticipantSet;

    private ParticipantTopology GetCachedParticipantTopology(
        DisseminationMembershipScope membershipScope,
        IEnumerable<SiloAddress> participants,
        SiloAddress? root,
        bool includeLocal)
    {
        var orderedParticipants = GetOrderedParticipants(participants, root, includeLocal, preserveOrder: true);

        lock (_topologyLock)
        {
            var current = membershipScope switch
            {
                DisseminationMembershipScope.AllMembers => _allMembersTopology,
                _ => _activeMembersTopology,
            };

            if (current.Participants.SequenceEqual(orderedParticipants))
            {
                return current;
            }

            var updated = BuildParticipantTopology(orderedParticipants);
            if (membershipScope == DisseminationMembershipScope.AllMembers)
            {
                _allMembersTopology = updated;
            }
            else
            {
                _activeMembersTopology = updated;
            }

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
        List<SiloAddress> orderedParticipants = [..preserveOrder
            ? participants.Distinct()
            : participants.Distinct().OrderBy(static participant => participant)];
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
            if (preserveOrder)
            {
                if (!orderedParticipants.Contains(participant))
                {
                    orderedParticipants.Add(participant);
                }
            }
            else if (!orderedParticipants.Contains(participant))
            {
                orderedParticipants.Add(participant);
            }
        }
    }

    private static ParticipantTopology BuildParticipantTopology(ImmutableArray<SiloAddress> sortedParticipants)
    {
        var indices = new Dictionary<SiloAddress, int>(sortedParticipants.Length);
        for (var i = 0; i < sortedParticipants.Length; i++)
        {
            indices[sortedParticipants[i]] = i;
        }

        return new ParticipantTopology(sortedParticipants, indices.ToFrozenDictionary(), sortedParticipants.ToFrozenSet());
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

    private bool IsExpired(DisseminationValue item) => item.ExpiresAt <= _timeProvider.GetUtcNow();

    private bool IsPeerBackedOff(SiloAddress peer)
    {
        var now = _timeProvider.GetUtcNow();
        lock (_failureLock)
        {
            return _failureBackoffUntil.TryGetValue(peer, out var until) && until > now;
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

    private DisseminationAntiEntropyResponse CreateAntiEntropyResponse(DisseminationValue[] values, bool truncated) => new()
    {
        Sender = _transport.LocalSilo,
        Values = values,
        Truncated = truncated,
    };

    private static DisseminationValue EnsureRoot(DisseminationValue item, SiloAddress root) =>
        item.Root is not null
            ? item
            : new DisseminationValue
            {
                Digest = item.Digest,
                Root = root,
                ExpiresAt = item.ExpiresAt,
                Payload = item.Payload,
            };

    private static bool IsRequested(IEnumerable<DisseminationCapabilityRequest> requests, DisseminationDigest digest)
    {
        foreach (var request in requests)
        {
            if (string.Equals(request.Topic, digest.Topic, StringComparison.Ordinal)
                && request.PayloadKinds.Contains(digest.PayloadKind, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<DisseminationDigest> SortDigests(IEnumerable<DisseminationDigest> digests) =>
        digests
            .OrderBy(static digest => digest.Topic, StringComparer.Ordinal)
            .ThenBy(static digest => digest.PayloadKind, StringComparer.Ordinal)
            .ThenBy(static digest => digest.Key, StringComparer.Ordinal)
            .ThenBy(static digest => digest.Version);

    private static DigestKey GetDigestKey(DisseminationDigest digest) => new(digest.Topic, digest.Key, digest.PayloadKind);

    public sealed record AntiEntropyState(
        FrozenDictionary<string, ImmutableArray<SiloAddress>> PeersByTopic,
        IReadOnlyList<DisseminationCapabilityRequest> Topics,
        IReadOnlyList<DisseminationDigest> Digests)
    {
        public static readonly AntiEntropyState Empty = new(
            FrozenDictionary<string, ImmutableArray<SiloAddress>>.Empty,
            [],
            []);
    }

    private readonly record struct CapabilityKey(SiloAddress Peer, string Topic);

    private readonly record struct CapabilityEntry(bool Supported, FrozenSet<string> PayloadKinds, DateTimeOffset ExpiresAt);

    private readonly record struct DigestKey(string Topic, string Key, string PayloadKind);

    private readonly record struct RequestedTopic(IDisseminationTopic Topic, FrozenSet<string> PayloadKinds);

    private enum CapabilityStatus
    {
        Supported,
        Unsupported,
        Unavailable,
    }

    private sealed record ParticipantTopology(
        ImmutableArray<SiloAddress> Participants,
        FrozenDictionary<SiloAddress, int> Indices,
        FrozenSet<SiloAddress> ParticipantSet)
    {
        public static readonly ParticipantTopology Empty = new(
            [],
            FrozenDictionary<SiloAddress, int>.Empty,
            FrozenSet<SiloAddress>.Empty);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination send to {Peer} failed.")]
    private static partial void LogDebugDisseminationSendFailed(ILogger logger, Exception exception, SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination capability probe to {Peer} for topic {Topic} failed.")]
    private static partial void LogDebugCapabilityProbeFailed(ILogger logger, Exception exception, SiloAddress peer, string topic);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to apply anti-entropy repair value from {Sender} for topic {Topic}, key {Key}, version {Version}.")]
    private static partial void LogDebugAntiEntropyRepairValueFailed(ILogger logger, Exception exception, SiloAddress sender, string topic, string key, long version);
}

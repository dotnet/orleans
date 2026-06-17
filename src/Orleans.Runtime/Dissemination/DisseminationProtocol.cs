using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationProtocol
{
    private readonly IDisseminationTransport _transport;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly Dictionary<string, IDisseminationTopic> _topics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger;
    private readonly Dictionary<CapabilityKey, CapabilityEntry> _capabilityCache = new();
    private readonly Dictionary<CapabilityKey, DateTimeOffset> _capabilityProbeBackoffUntil = new();
    private readonly Dictionary<SiloAddress, DateTimeOffset> _failureBackoffUntil = new();
    private readonly object _capabilityLock = new();
    private readonly object _failureLock = new();
    private readonly object _topologyLock = new();
    private readonly Dictionary<SiloAddress, string> _peerScores = new();
    private ParticipantTopology _participantTopology = ParticipantTopology.Empty;

    public DisseminationProtocol(
        IDisseminationTransport transport,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationTopic> topics,
        TimeProvider timeProvider,
        ILogger<DisseminationProtocol> logger)
    {
        _transport = transport;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _topics = topics.ToDictionary(static topic => topic.Name, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, IDisseminationTopic> Topics => _topics;

    public async ValueTask<bool> Publish(
        string topicName,
        DisseminationItem item,
        IReadOnlyCollection<SiloAddress>? targetPeers,
        CancellationToken cancellationToken)
    {
        if (!TryGetEnabledTopic(topicName, out var topic))
        {
            return false;
        }

        if (!ValidatePayloadSize(topic, item))
        {
            await topic.OnFallbackRequired(default!, item.Id, cancellationToken);
            DisseminationInstruments.OnFallback(item.Id.Topic, "oversize");
            return false;
        }

        var root = item.Root is not null ? item.Root : _transport.LocalSilo;
        item = EnsureRoot(item, root);
        var peers = targetPeers is { Count: > 0 } ? targetPeers : _transport.GetActivePeers();
        var topology = GetParticipantTopology(peers.Append(root), includeLocal: true);
        if (!await AreParticipantsCapable(topology.Participants, topic, item.Id.PayloadKind, cancellationToken))
        {
            return false;
        }

        foreach (var peer in GetTreeChildren(topology, root))
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

        foreach (var group in batch.Items.GroupBy(static item => item.Id.Topic))
        {
            DisseminationInstruments.OnGossipReceived(group.Key, "tree", group.Count());
        }

        foreach (var item in batch.Items)
        {
            await ApplyReceivedItem(item, batch.Sender, forward: true, cancellationToken);
        }
    }

    public AntiEntropyState CreateAntiEntropyState()
    {
        if (!_options.CurrentValue.Enabled)
        {
            return AntiEntropyState.Empty;
        }

        var topics = new List<DisseminationCapabilityRequest>();
        var digests = new List<DisseminationItemId>();
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
                PayloadKinds = topic.PayloadKinds.ToArray(),
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

        return new AntiEntropyState(
            SelectAntiEntropyPeers(),
            topics.ToArray(),
            SortDigests(digests).ToArray());
    }

    public async Task<IReadOnlyList<DisseminationAntiEntropyResponse>> ExchangeAntiEntropy(
        AntiEntropyState state,
        CancellationToken cancellationToken)
    {
        if (state.Peers.Count == 0 || state.Topics.Count == 0)
        {
            return Array.Empty<DisseminationAntiEntropyResponse>();
        }

        var responses = new List<DisseminationAntiEntropyResponse>(state.Peers.Count);
        foreach (var peer in state.Peers)
        {
            var topics = await GetCapableAntiEntropyTopics(peer, state.Topics, cancellationToken);
            if (topics.Count == 0)
            {
                continue;
            }

            var digests = state.Digests.Where(digest => IsRequested(topics, digest)).ToArray();
            var request = new DisseminationAntiEntropyRequest
            {
                Sender = _transport.LocalSilo,
                Topics = topics.ToArray(),
                Digests = digests,
            };

            var response = await SafeRequest(peer, target => _transport.ExchangeAntiEntropy(target, request, cancellationToken));
            if (response is null)
            {
                continue;
            }

            DisseminationInstruments.OnAntiEntropyExchange("out", request.Digests.Length, response.Items.Length, response.Truncated);
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
            foreach (var item in response.Items)
            {
                try
                {
                    await ApplyReceivedItem(item, response.Sender, forward: false, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    LogDebugAntiEntropyRepairItemFailed(_logger, exception, response.Sender, item.Id.Topic, item.Id.Key.ToString(), item.Id.Version);
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
            return CreateAntiEntropyResponse(Array.Empty<DisseminationItem>(), truncated: false);
        }

        var requestedTopics = GetRequestedTopics(request);
        if (requestedTopics.Count == 0)
        {
            return CreateAntiEntropyResponse(Array.Empty<DisseminationItem>(), truncated: false);
        }

        var remoteDigests = GetRemoteDigestMap(request.Digests);
        var items = new List<DisseminationItem>();
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
                if (remoteDigests.TryGetValue(digestKey, out var remoteDigest)
                    && requestedTopic.Topic.CompareVersion(localDigest, remoteDigest) <= 0)
                {
                    continue;
                }

                var item = await requestedTopic.Topic.GetItem(localDigest, cancellationToken);
                if (item is null || !ValidatePayloadSize(requestedTopic.Topic, item))
                {
                    continue;
                }

                if (items.Count >= options.MaxBatchItems || byteCount + item.Payload.Length > options.MaxBatchBytes)
                {
                    truncated = true;
                    break;
                }

                items.Add(item);
                byteCount += item.Payload.Length;
            }

            if (truncated)
            {
                break;
            }
        }

        DisseminationInstruments.OnAntiEntropyExchange("in", request.Digests.Length, items.Count, truncated);
        return CreateAntiEntropyResponse(items.ToArray(), truncated);
    }

    private async ValueTask<DisseminationApplyResult> ApplyReceivedItem(
        DisseminationItem item,
        SiloAddress sender,
        bool forward,
        CancellationToken cancellationToken)
    {
        if (!TryGetEnabledTopic(item.Id.Topic, out var topic))
        {
            EmitApplyResult(item, sender, DisseminationApplyResult.Rejected);
            return DisseminationApplyResult.Rejected;
        }

        if (!topic.PayloadKinds.Contains(item.Id.PayloadKind))
        {
            EmitApplyResult(item, sender, DisseminationApplyResult.Rejected);
            return DisseminationApplyResult.Rejected;
        }

        if (!ValidatePayloadSize(topic, item))
        {
            return DisseminationApplyResult.Rejected;
        }

        if (IsExpired(item) || topic.IsObsolete(item.Id))
        {
            EmitApplyResult(item, sender, DisseminationApplyResult.Obsolete);
            return DisseminationApplyResult.Obsolete;
        }

        var result = await topic.ApplyItem(item, cancellationToken);
        EmitApplyResult(item, sender, result);

        if (result == DisseminationApplyResult.Applied && forward)
        {
            await Forward(item, sender, cancellationToken);
        }

        return result;
    }

    private async Task Forward(DisseminationItem item, SiloAddress sender, CancellationToken cancellationToken)
    {
        var root = item.Root is not null ? item.Root : sender;
        item = EnsureRoot(item, root);
        var topology = GetParticipantTopology(_transport.GetActivePeers().Append(root), includeLocal: true);
        foreach (var peer in GetTreeChildren(topology, root))
        {
            if (!Equals(peer, sender))
            {
                await SendGossip(peer, item, cancellationToken);
            }
        }
    }

    private async ValueTask<bool> SendGossip(SiloAddress peer, DisseminationItem item, CancellationToken cancellationToken)
    {
        if (!_topics.TryGetValue(item.Id.Topic, out var topic)
            || !await IsCapable(peer, topic, item.Id.PayloadKind, cancellationToken))
        {
            return false;
        }

        var batch = new DisseminationGossipBatch
        {
            Sender = _transport.LocalSilo,
            Items = new[] { item },
        };

        var sent = await SafeSend(peer, target => _transport.SendGossip(target, batch, cancellationToken));
        if (sent)
        {
            DisseminationInstruments.OnGossipSent(item.Id.Topic, "tree", 1, item.Payload.Length);
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

    private async ValueTask<bool> IsCapable(
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
                return cached.Supported && cached.PayloadKinds.Contains(payloadKind);
            }

            if (_capabilityProbeBackoffUntil.TryGetValue(key, out var probeBackoffUntil))
            {
                if (probeBackoffUntil > now)
                {
                    return false;
                }

                _capabilityProbeBackoffUntil.Remove(key);
            }
        }

        var request = new DisseminationCapabilityRequest
        {
            Topic = topic.Name,
            ProtocolVersion = topic.ProtocolVersion,
            PayloadKinds = topic.PayloadKinds.ToArray(),
        };

        try
        {
            var response = await _transport.GetCapabilities(peer, request, cancellationToken);
            var supported = response.Supported && response.ProtocolVersion >= topic.ProtocolVersion;
            var payloadKinds = response.PayloadKinds.ToHashSet(StringComparer.Ordinal);
            lock (_capabilityLock)
            {
                _capabilityCache[key] = new CapabilityEntry(supported, payloadKinds, now + _options.CurrentValue.CapabilityCacheTtl);
                _capabilityProbeBackoffUntil.Remove(key);
            }

            DisseminationEvents.EmitCapabilityProbe(_transport.LocalSilo, peer, topic.Name, supported);
            return supported && payloadKinds.Contains(payloadKind);
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
            return false;
        }
    }

    private async ValueTask<bool> AreParticipantsCapable(
        IReadOnlyList<SiloAddress> participants,
        IDisseminationTopic topic,
        string payloadKind,
        CancellationToken cancellationToken)
    {
        var probes = new List<Task<bool>>(participants.Count);
        foreach (var peer in participants)
        {
            if (!Equals(peer, _transport.LocalSilo))
            {
                probes.Add(IsCapable(peer, topic, payloadKind, cancellationToken).AsTask());
            }
        }

        if (probes.Count == 0)
        {
            return true;
        }

        var results = await Task.WhenAll(probes);
        return results.All(static supported => supported);
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
                if (await IsCapable(peer, topic, payloadKind, cancellationToken))
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
                    PayloadKinds = supportedPayloadKinds.ToArray(),
                });
            }
        }

        return result;
    }

    private Dictionary<string, RequestedTopic> GetRequestedTopics(DisseminationAntiEntropyRequest request)
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
                .ToHashSet(StringComparer.Ordinal);
            if (payloadKinds.Count > 0)
            {
                result[topic.Name] = new RequestedTopic(topic, payloadKinds);
            }
        }

        return result;
    }

    private Dictionary<DigestKey, DisseminationItemId> GetRemoteDigestMap(IEnumerable<DisseminationItemId> digests)
    {
        var result = new Dictionary<DigestKey, DisseminationItemId>();
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

        return result;
    }

    private IReadOnlyList<SiloAddress> SelectAntiEntropyPeers()
    {
        var options = _options.CurrentValue.Overlay;
        var topology = GetParticipantTopology(_transport.GetActivePeers(), includeLocal: true);
        var participants = topology.Participants;
        if (participants.Count <= 1)
        {
            return Array.Empty<SiloAddress>();
        }

        if (!topology.Indices.TryGetValue(_transport.LocalSilo, out var localIndex))
        {
            return Array.Empty<SiloAddress>();
        }

        var result = new List<SiloAddress>(Math.Min(options.AntiEntropyPeerCount, participants.Count - 1));
        for (var offset = 1; offset < participants.Count && result.Count < options.AntiEntropyPeerCount; offset++)
        {
            var peer = participants[(localIndex + offset) % participants.Count];
            if (!Equals(peer, _transport.LocalSilo))
            {
                result.Add(peer);
            }
        }

        return result;
    }

    private ParticipantTopology GetParticipantTopology(IEnumerable<SiloAddress> peers, bool includeLocal)
    {
        var peerSet = new HashSet<SiloAddress>();
        if (includeLocal)
        {
            peerSet.Add(_transport.LocalSilo);
        }

        foreach (var peer in peers)
        {
            if (peer is not null)
            {
                peerSet.Add(peer);
            }
        }

        lock (_topologyLock)
        {
            if (_participantTopology.ParticipantSet.SetEquals(peerSet))
            {
                return _participantTopology;
            }

            var participants = peerSet
                .OrderBy(GetPeerScoreLocked, StringComparer.Ordinal)
                .ToList();
            var indices = new Dictionary<SiloAddress, int>(participants.Count);
            for (var i = 0; i < participants.Count; i++)
            {
                indices[participants[i]] = i;
            }

            _participantTopology = new ParticipantTopology(participants, indices, peerSet);
            return _participantTopology;
        }
    }

    private string GetPeerScoreLocked(SiloAddress peer)
    {
        if (!_peerScores.TryGetValue(peer, out var score))
        {
            score = ComputePeerScore(peer);
            _peerScores[peer] = score;
        }

        return score;
    }

    private IReadOnlyList<SiloAddress> GetTreeChildren(ParticipantTopology topology, SiloAddress root)
    {
        if (!topology.Indices.TryGetValue(root, out var rootIndex)
            || !topology.Indices.TryGetValue(_transport.LocalSilo, out var localIndex))
        {
            return Array.Empty<SiloAddress>();
        }

        var fanout = _options.CurrentValue.Overlay.TreeFanout;
        var count = topology.Participants.Count;
        var localTreeIndex = localIndex >= rootIndex ? localIndex - rootIndex : localIndex + count - rootIndex;
        var firstChild = (localTreeIndex * fanout) + 1;
        if (firstChild >= count)
        {
            return Array.Empty<SiloAddress>();
        }

        var result = new List<SiloAddress>(Math.Min(fanout, count - firstChild));
        for (var i = 0; i < fanout; i++)
        {
            var childTreeIndex = firstChild + i;
            if (childTreeIndex >= count)
            {
                break;
            }

            result.Add(topology.Participants[(rootIndex + childTreeIndex) % count]);
        }

        return result;
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

    private bool ValidatePayloadSize(IDisseminationTopic topic, DisseminationItem item)
    {
        var options = _options.CurrentValue;
        if (item.Payload.Length > topic.Options.MaxPayloadBytes || item.Payload.Length > options.MaxBatchBytes)
        {
            DisseminationEvents.EmitPayloadDrop(item.Id, _transport.LocalSilo, "oversize", item.Payload.Length);
            DisseminationInstruments.OnPayloadDropped(item.Id.Topic, "oversize");
            return false;
        }

        return true;
    }

    private bool IsExpired(DisseminationItem item) => item.ExpiresAt <= _timeProvider.GetUtcNow();

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

    private void EmitApplyResult(DisseminationItem item, SiloAddress sender, DisseminationApplyResult result)
    {
        DisseminationEvents.EmitItem(item.Id, _transport.LocalSilo, sender, result.ToString(), item.Payload.Length);
        DisseminationInstruments.OnItemApplied(item.Id.Topic, result.ToString());
    }

    private DisseminationAntiEntropyResponse CreateAntiEntropyResponse(DisseminationItem[] items, bool truncated) => new()
    {
        Sender = _transport.LocalSilo,
        Items = items,
        Truncated = truncated,
    };

    private static DisseminationItem EnsureRoot(DisseminationItem item, SiloAddress root) =>
        item.Root is not null
            ? item
            : new DisseminationItem
            {
                Id = item.Id,
                Root = root,
                ExpiresAt = item.ExpiresAt,
                Payload = item.Payload,
            };

    private static bool IsRequested(IEnumerable<DisseminationCapabilityRequest> requests, DisseminationItemId digest)
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

    private static IEnumerable<DisseminationItemId> SortDigests(IEnumerable<DisseminationItemId> digests) =>
        digests
            .OrderBy(static digest => digest.Topic, StringComparer.Ordinal)
            .ThenBy(static digest => digest.PayloadKind, StringComparer.Ordinal)
            .ThenBy(static digest => digest.Key.ToString(), StringComparer.Ordinal)
            .ThenBy(static digest => digest.Version);

    private static DigestKey GetDigestKey(DisseminationItemId digest) => new(digest.Topic, digest.Key, digest.PayloadKind);

    private static string ComputePeerScore(SiloAddress peer)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(peer.ToParsableString()));
        return Convert.ToHexString(bytes);
    }

    public sealed record AntiEntropyState(
        IReadOnlyList<SiloAddress> Peers,
        IReadOnlyList<DisseminationCapabilityRequest> Topics,
        IReadOnlyList<DisseminationItemId> Digests)
    {
        public static readonly AntiEntropyState Empty = new(
            Array.Empty<SiloAddress>(),
            Array.Empty<DisseminationCapabilityRequest>(),
            Array.Empty<DisseminationItemId>());
    }

    private readonly record struct CapabilityKey(SiloAddress Peer, string Topic);

    private readonly record struct CapabilityEntry(bool Supported, HashSet<string> PayloadKinds, DateTimeOffset ExpiresAt);

    private readonly record struct DigestKey(string Topic, DisseminationValueKey Key, string PayloadKind);

    private readonly record struct RequestedTopic(IDisseminationTopic Topic, HashSet<string> PayloadKinds);

    private sealed record ParticipantTopology(
        IReadOnlyList<SiloAddress> Participants,
        IReadOnlyDictionary<SiloAddress, int> Indices,
        HashSet<SiloAddress> ParticipantSet)
    {
        public static readonly ParticipantTopology Empty = new(
            Array.Empty<SiloAddress>(),
            new Dictionary<SiloAddress, int>(),
            new HashSet<SiloAddress>());
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
        Message = "Failed to apply anti-entropy repair item from {Sender} for topic {Topic}, key {Key}, version {Version}.")]
    private static partial void LogDebugAntiEntropyRepairItemFailed(ILogger logger, Exception exception, SiloAddress sender, string topic, string key, long version);
}

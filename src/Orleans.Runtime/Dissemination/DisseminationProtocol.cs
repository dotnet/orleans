using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
    private readonly DisseminationBroadcastQueue _broadcastQueue;
    private readonly Dictionary<SiloAddress, DateTimeOffset> _failureBackoffUntil = [];
    private readonly object _failureLock = new();
    private readonly object _seenValueLock = new();
    private readonly Dictionary<DigestKey, DateTimeOffset> _lastValueSeenAt = [];
    private readonly FrozenDictionary<DisseminationNamespace, IDisseminationNamespace> _namespaces;

    public DisseminationProtocol(
        IDisseminationTransport transport,
        DisseminationMembership membership,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationNamespace> disseminationNamespaces,
        TimeProvider timeProvider,
        ILogger<DisseminationProtocol> logger)
    {
        _transport = transport;
        _membership = membership;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _namespaces = disseminationNamespaces.ToFrozenDictionary(static ns => ns.Name);
        _broadcastQueue = new DisseminationBroadcastQueue(
            timeProvider,
            async (batches, cancellationToken) => await Parallel.ForEachAsync(
                batches,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = _options.CurrentValue.MaxConcurrentSends,
                },
                async (queued, operationCancellationToken) => await SendBroadcastBatch(queued.Peer, queued.ValuesByNamespace, operationCancellationToken)),
            exception => LogDebugBroadcastFlushFailed(_logger, exception));
    }

    public async ValueTask<bool> Publish(
        IDisseminationNamespace disseminationNamespace,
        DisseminationValue value,
        CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled || !disseminationNamespace.Options.Enabled)
        {
            return false;
        }

        var item = CreateBroadcastValue(disseminationNamespace, value);
        if (!TryValidatePublishValue(disseminationNamespace, item, out var reason))
        {
            DisseminationInstruments.OnFallback(disseminationNamespace.Name, reason);
            return false;
        }

        var membership = await GetMembershipSnapshotForRouting(disseminationNamespace.Group, _transport.LocalSilo, cancellationToken);
        if (membership is null)
        {
            return false;
        }

        var treeTargets = membership.GetOriginatorTreeTargets(disseminationNamespace.Group);
        RecordSeenValue(disseminationNamespace.Name, value.Key);
        foreach (var peer in treeTargets)
        {
            EnqueueBroadcast(peer, item, disseminationNamespace);
        }

        return true;
    }

    public async Task ReceiveBroadcast(DisseminationBroadcastBatch batch, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        DisseminationInstruments.OnBroadcastReceived(batch.Values, "tree");

        foreach (var (namespaceName, values) in batch.Values)
        {
            if (!TryGetEnabledNamespace(namespaceName, out var disseminationNamespace))
            {
                continue;
            }

            foreach (var item in values)
            {
                await ApplyReceivedValue(disseminationNamespace, item, batch.Sender, forward: true, cancellationToken);
            }
        }
    }

    public async Task RunAntiEntropyRound(CancellationToken cancellationToken)
    {
        (SiloAddress Peer, DisseminationAntiEntropyRequest Request)[]? requests = [];
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        var membership = _membership.CurrentSnapshot;
        PrunePeerState(membership);
        var peersByGroup = new Dictionary<DisseminationGroup, (SiloAddress[] Peers, int Count)>();
        var requestsByPeer = new Dictionary<SiloAddress, Dictionary<DisseminationNamespace, ImmutableArray<DigestEntry>>>();
        var currentValueStreams = new HashSet<DigestKey>();
        var now = _timeProvider.GetUtcNow();
        foreach (var disseminationNamespace in _namespaces.Values)
        {
            if (!disseminationNamespace.Options.Enabled)
            {
                continue;
            }

            ImmutableArray<DigestEntry>.Builder? digests = null;
            foreach (var (key, version) in disseminationNamespace.GetDigest().OrderBy(static entry => entry.Key))
            {
                var digestKey = new DigestKey(disseminationNamespace.Name, key);
                currentValueStreams.Add(digestKey);
                if (ShouldRequestAntiEntropy(disseminationNamespace, digestKey, now))
                {
                    (digests ??= ImmutableArray.CreateBuilder<DigestEntry>()).Add(new DigestEntry(key, version));
                }
            }

            if (digests is null)
            {
                continue;
            }

            ref var peers = ref CollectionsMarshal.GetValueRefOrAddDefault(peersByGroup, disseminationNamespace.Group, out var exists);
            if (!exists)
            {
                peers = SelectAntiEntropyPeers(
                    membership,
                    disseminationNamespace.Group,
                    _options.CurrentValue.Overlay.AntiEntropyPeerCount);
            }

            if (peers.Count == 0)
            {
                continue;
            }

            var digestEntries = digests.ToImmutable();
            for (var i = 0; i < peers.Count; i++)
            {
                var peer = peers.Peers[i];
                ref var pendingRequest = ref CollectionsMarshal.GetValueRefOrAddDefault(requestsByPeer, peer, out _);
                (pendingRequest ??= [])[disseminationNamespace.Name] = digestEntries;
            }

            PruneSeenValues(currentValueStreams);
            if (requestsByPeer.Count == 0)
            {
                return;
            }

            requests = new (SiloAddress Peer, DisseminationAntiEntropyRequest Request)[requestsByPeer.Count];
            var index = 0;
            foreach (var (peer, digest) in requestsByPeer)
            {
                requests[index++] = (peer, new DisseminationAntiEntropyRequest { Digests = digest.ToFrozenDictionary(), });
            }

            if (requests.Length == 0)
            {
                return;
            }

            var responses = new DisseminationAntiEntropyResponse?[requests.Length];
            await Parallel.ForAsync(
                0,
                requests.Length,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = _options.CurrentValue.MaxConcurrentSends,
                },
                async (index, operationCancellationToken) =>
                {
                    var (peer, request) = requests[index];
                    var response = await ExecutePeerOperation(
                        peer,
                        async target =>
                            await _transport.ExchangeAntiEntropy(target, request, operationCancellationToken),
                        failureResult: null, cancellationToken: operationCancellationToken);
                    if (response is null)
                    {
                        return;
                    }

                    DisseminationInstruments.OnAntiEntropyExchange(
                        "out",
                        GetDigestCount(request.Digests),
                        GetValueCount(response.Values),
                        response.Truncated);
                    responses[index] = response;
                });

            foreach (var response in responses)
            {
                if (response is null)
                {
                    continue;
                }

                foreach (var (namespaceName, values) in response.Values)
                {
                    if (!TryGetEnabledNamespace(namespaceName, out var ns))
                    {
                        continue;
                    }

                    foreach (var item in values)
                    {
                        try
                        {
                            await ApplyReceivedValue(ns, item, response.Sender, forward: false, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            LogDebugAntiEntropyRepairValueFailed(_logger, exception, response.Sender, namespaceName,
                                item.Value.Key, item.Value.ToVersion);
                        }
                    }
                }
            }
        }
    }

    private static (SiloAddress[] Peers, int Count) SelectAntiEntropyPeers(
        DisseminationMembershipSnapshot membership,
        DisseminationGroup group,
        int peerCount)
    {
        var result = new SiloAddress[peerCount];
        var peers = result.AsSpan();
        membership.SelectAntiEntropyPeers(group, ref peers);
        return (result, peers.Length);
    }

    public ValueTask<DisseminationAntiEntropyResponse> ReceiveAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken) => new(ReceiveAntiEntropyCore(request));

    private DisseminationAntiEntropyResponse ReceiveAntiEntropyCore(DisseminationAntiEntropyRequest request)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return new DisseminationAntiEntropyResponse
            {
                Sender = _transport.LocalSilo,
                Values = FrozenDictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>>.Empty,
                Truncated = false,
            };
        }

        var requestDigestCount = GetDigestCount(request.Digests);
        var valueCount = 0;
        var byteCount = 0;
        var truncated = false;
        var options = _options.CurrentValue;

        var valuesByNamespace = new Dictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>>();
        foreach (var (namespaceName, remoteDigest) in request.Digests)
        {
            if (!TryGetEnabledNamespace(namespaceName, out var requestedNamespace))
            {
                continue;
            }

            if (remoteDigest.Length == 0)
            {
                continue;
            }

            var namespaceValues = ImmutableArray.CreateBuilder<DisseminationBroadcastValue>();
            var remoteVersions = CreateDigestLookup(remoteDigest);
            foreach (var (key, localVersion) in requestedNamespace.GetDigest())
            {
                if (!remoteVersions.TryGetValue(key, out var peerVersion))
                {
                    continue;
                }

                if (localVersion <= peerVersion)
                {
                    continue;
                }

                if (!requestedNamespace.TryCreateRepairValue(
                    key,
                    peerVersion,
                    out var value))
                {
                    continue;
                }

                var item = CreateBroadcastValue(requestedNamespace, value);
                if (!ValidatePayloadSize(requestedNamespace, item))
                {
                    continue;
                }

                if (valueCount >= options.MaxBatchItems || byteCount + item.Value.Payload.Length > options.MaxBatchBytes)
                {
                    truncated = true;
                    break;
                }

                namespaceValues.Add(item);
                ++valueCount;
                byteCount += item.Value.Payload.Length;
            }

            valuesByNamespace[requestedNamespace.Name] = namespaceValues.ToImmutable();
            if (truncated)
            {
                break;
            }
        }

        DisseminationInstruments.OnAntiEntropyExchange("in", requestDigestCount, valueCount, truncated);
        return new DisseminationAntiEntropyResponse
        {
            Sender = _transport.LocalSilo,
            Values = valuesByNamespace.ToFrozenDictionary(),
            Truncated = truncated,
        };
    }

    private async ValueTask<DisseminationApplyResult> ApplyReceivedValue(
        IDisseminationNamespace disseminationNamespace,
        DisseminationBroadcastValue item,
        SiloAddress sender,
        bool forward,
        CancellationToken cancellationToken)
    {
        var namespaceName = disseminationNamespace.Name;
        if (!ValidatePayloadSize(disseminationNamespace, item))
        {
            return DisseminationApplyResult.Rejected;
        }

        if (IsExpired(item))
        {
            EmitApplyResult(namespaceName, item, sender, DisseminationApplyResult.Obsolete);
            return DisseminationApplyResult.Obsolete;
        }

        if (GetApplicability(disseminationNamespace, item.Value) is { } applicabilityResult)
        {
            EmitApplyResult(namespaceName, item, sender, applicabilityResult);
            if (applicabilityResult == DisseminationApplyResult.Duplicate)
            {
                RecordSeenValue(namespaceName, item.Value.Key);
            }

            return applicabilityResult;
        }

        var result = await disseminationNamespace.ApplyValueAsync(item.Value, cancellationToken);
        EmitApplyResult(namespaceName, item, sender, result);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            RecordSeenValue(namespaceName, item.Value.Key);
        }

        if (result == DisseminationApplyResult.Applied && forward)
        {
            await Forward(item, disseminationNamespace, sender, cancellationToken);
        }

        return result;
    }

    private async Task Forward(DisseminationBroadcastValue item, IDisseminationNamespace disseminationNamespace, SiloAddress sender, CancellationToken cancellationToken)
    {
        var originator = item.Originator;
        var membership = await GetMembershipSnapshotForRouting(disseminationNamespace.Group, originator, cancellationToken);
        if (membership is null)
        {
            return;
        }

        foreach (var peer in membership.GetForwardingTreeTargets(disseminationNamespace.Group))
        {
            if (Equals(peer, originator) || Equals(peer, sender))
            {
                continue;
            }

            EnqueueBroadcast(peer, item, disseminationNamespace);
        }
    }

    internal async Task FlushPendingBroadcast(CancellationToken cancellationToken)
    {
        await _broadcastQueue.FlushPendingBroadcast(cancellationToken);
    }

    private void EnqueueBroadcast(SiloAddress peer, DisseminationBroadcastValue item, IDisseminationNamespace disseminationNamespace)
    {
        _broadcastQueue.Enqueue(
            peer,
            item,
            disseminationNamespace,
            _options.CurrentValue.MaxBatchItems,
            _options.CurrentValue.MaxBatchBytes);
    }

    private async Task SendBroadcastBatch(SiloAddress peer, IReadOnlyList<DisseminationBroadcastQueue.PendingNamespaceValues> valuesByNamespace, CancellationToken cancellationToken)
    {
        var currentBatch = new Dictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>.Builder>();
        var itemCount = 0;
        var byteCount = 0;
        foreach (var group in valuesByNamespace)
        {
            if (!TryGetEnabledNamespace(group.Namespace, out _))
            {
                continue;
            }

            foreach (var item in group.Values)
            {
                if (itemCount > 0
                    && (itemCount >= _options.CurrentValue.MaxBatchItems
                        || byteCount + item.Value.Payload.Length > _options.CurrentValue.MaxBatchBytes))
                {
                    await SendBroadcastBatchCore(peer, currentBatch.ToFrozenDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value.ToImmutable()), cancellationToken);
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
            await SendBroadcastBatchCore(peer, currentBatch.ToFrozenDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable()), cancellationToken);
        }
    }

    private async Task SendBroadcastBatchCore(
        SiloAddress peer,
        FrozenDictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>> valuesByNamespace,
        CancellationToken cancellationToken)
    {
        var batch = new DisseminationBroadcastBatch
        {
            Sender = _transport.LocalSilo,
            Values = valuesByNamespace,
        };

        await ExecutePeerOperation(
            peer,
            async target =>
            {
                await _transport.SendBroadcast(target, batch, cancellationToken);
                DisseminationInstruments.OnBroadcastSent(batch.Values, "tree");
                return true;
            },
            failureResult: false,
            cancellationToken: cancellationToken);
    }

    private async ValueTask<T> ExecutePeerOperation<T>(SiloAddress peer,
        Func<SiloAddress, ValueTask<T>> operation,
        T failureResult,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsPeerBackedOff(peer))
        {
            return failureResult;
        }

        try
        {
            var result = await operation(peer);
            lock (_failureLock)
            {
                _failureBackoffUntil.Remove(peer);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogDebugDisseminationSendFailed(_logger, exception, peer);
            lock (_failureLock)
            {
                _failureBackoffUntil[peer] = _timeProvider.GetUtcNow() + _options.CurrentValue.FailureBackoff;
            }

            return failureResult;
        }
    }

    private bool ShouldRequestAntiEntropy(IDisseminationNamespace disseminationNamespace, DigestKey key, DateTimeOffset now)
    {
        lock (_seenValueLock)
        {
            return !_lastValueSeenAt.TryGetValue(key, out var lastSeen)
                || now - lastSeen >= disseminationNamespace.Options.ExpectedUpdateCadence;
        }
    }

    private void RecordSeenValue(DisseminationNamespace namespaceName, DisseminationKey key)
    {
        var digestKey = new DigestKey(namespaceName, key);
        lock (_seenValueLock)
        {
            _lastValueSeenAt[digestKey] = _timeProvider.GetUtcNow();
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

            if (removedKeys is null)
            {
                return;
            }

            foreach (var key in removedKeys)
            {
                _lastValueSeenAt.Remove(key);
            }
        }
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

        _broadcastQueue.Prune(membershipSnapshot, _transport.LocalSilo);
    }

    private async ValueTask<DisseminationMembershipSnapshot?> GetMembershipSnapshotForRouting(
        DisseminationGroup group,
        SiloAddress originator,
        CancellationToken cancellationToken)
    {
        if (!_membership.CurrentSnapshot.ContainsMember(originator, group))
        {
            LogDebugDisseminationOriginatorMissing(_logger, originator);
        }

        var membership = await _membership.GetSnapshotContainingMember(group, originator, cancellationToken);
        PrunePeerState(membership ?? _membership.CurrentSnapshot);
        return membership;
    }

    private bool TryGetEnabledNamespace(DisseminationNamespace namespaceName, [NotNullWhen(true)] out IDisseminationNamespace? disseminationNamespace)
    {
        if (_options.CurrentValue.Enabled
            && _namespaces.TryGetValue(namespaceName, out disseminationNamespace)
            && disseminationNamespace.Options.Enabled)
        {
            return true;
        }

        disseminationNamespace = null;
        return false;
    }

    private bool ValidatePayloadSize(IDisseminationNamespace disseminationNamespace, DisseminationBroadcastValue item)
    {
        var options = _options.CurrentValue;
        if (item.Value.Payload.Length <= disseminationNamespace.Options.MaxPayloadBytes &&
            item.Value.Payload.Length <= options.MaxBatchBytes)
        {
            return true;
        }

        var namespaceName = disseminationNamespace.Name;
        DisseminationEvents.EmitPayloadDrop(namespaceName, item.Value, _transport.LocalSilo, "oversize", item.Value.Payload.Length);
        DisseminationInstruments.OnPayloadDropped(namespaceName, "oversize");
        return false;

    }

    private bool TryValidatePublishValue(
        IDisseminationNamespace disseminationNamespace,
        DisseminationBroadcastValue item,
        [NotNullWhen(false)] out string? failureReason)
    {
        if (IsExpired(item))
        {
            failureReason = "expired";
            return false;
        }

        if (!TryValidateVersionRange(item.Value, out failureReason))
        {
            return false;
        }

        if (disseminationNamespace.GetVersion(item.Value.Key) > item.Value.ToVersion)
        {
            failureReason = "obsolete";
            return false;
        }

        if (!ValidatePayloadSize(disseminationNamespace, item))
        {
            failureReason = "oversize";
            return false;
        }

        failureReason = null;
        return true;
    }

    private static bool TryValidateVersionRange(DisseminationValue value, [NotNullWhen(false)] out string? failureReason)
    {
        if (value.FromVersion < 0 || value.ToVersion <= 0 || value.ToVersion <= value.FromVersion)
        {
            failureReason = "invalid-version";
            return false;
        }

        failureReason = null;
        return true;
    }

    private static DisseminationApplyResult? GetApplicability(IDisseminationNamespace disseminationNamespace, DisseminationValue value)
    {
        if (!TryValidateVersionRange(value, out _))
        {
            return DisseminationApplyResult.Rejected;
        }

        var localVersion = disseminationNamespace.GetVersion(value.Key);
        if (value.ToVersion < localVersion)
        {
            return DisseminationApplyResult.Obsolete;
        }

        if (value.ToVersion == localVersion)
        {
            return DisseminationApplyResult.Duplicate;
        }

        return value.FromVersion == 0 || value.FromVersion == localVersion
            ? null
            : DisseminationApplyResult.Rejected;
    }

    private bool IsExpired(DisseminationBroadcastValue item) => item.ExpiresAt <= _timeProvider.GetUtcNow();

    private DisseminationBroadcastValue CreateBroadcastValue(
        IDisseminationNamespace disseminationNamespace,
        DisseminationValue value) => new()
    {
        Value = value,
        Originator = _transport.LocalSilo,
        ExpiresAt = _timeProvider.GetUtcNow() + disseminationNamespace.Options.StaleItemTtl,
    };

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

    private void EmitApplyResult(DisseminationNamespace namespaceName, DisseminationBroadcastValue item, SiloAddress sender, DisseminationApplyResult result)
    {
        DisseminationEvents.EmitValue(namespaceName, item.Value, _transport.LocalSilo, sender, result, item.Value.Payload.Length);
        DisseminationInstruments.OnValueApplied(namespaceName, result);
    }

    private static int GetDigestCount(FrozenDictionary<DisseminationNamespace, ImmutableArray<DigestEntry>> digest)
    {
        var result = 0;
        foreach (var entries in digest.Values)
        {
            result += entries.Length;
        }

        return result;
    }

    private static Dictionary<DisseminationKey, long> CreateDigestLookup(ImmutableArray<DigestEntry> digest)
    {
        var result = new Dictionary<DisseminationKey, long>(digest.Length);
        foreach (var entry in digest)
        {
            result[entry.Key] = entry.Version;
        }

        return result;
    }

    private static int GetValueCount(FrozenDictionary<DisseminationNamespace, ImmutableArray<DisseminationBroadcastValue>> valuesByNamespace)
    {
        var result = 0;
        foreach (var values in valuesByNamespace.Values)
        {
            result += values.Length;
        }

        return result;
    }

    private readonly record struct DigestKey(DisseminationNamespace Namespace, DisseminationKey Key);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination send to {Peer} failed.")]
    private static partial void LogDebugDisseminationSendFailed(ILogger logger, Exception exception, SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to apply anti-entropy repair value from {Sender} for namespace {Namespace}, key {Key}, version {Version}.")]
    private static partial void LogDebugAntiEntropyRepairValueFailed(ILogger logger, Exception exception, SiloAddress sender, DisseminationNamespace @namespace, DisseminationKey key, long version);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination broadcast batch flush failed.")]
    private static partial void LogDebugBroadcastFlushFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination originator {Originator} is missing from local membership; refreshing membership before routing.")]
    private static partial void LogDebugDisseminationOriginatorMissing(ILogger logger, SiloAddress originator);
}

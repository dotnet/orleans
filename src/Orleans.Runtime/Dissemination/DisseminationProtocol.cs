using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationProtocol
{
    private readonly SiloAddress _localSilo;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly DisseminationMembership _membership;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger;
    private readonly DisseminationBroadcastQueue _broadcastQueue;
    private readonly object _seenValueLock = new();
    private readonly Dictionary<DigestKey, DateTimeOffset> _lastValueSeenAt = [];
    private readonly FrozenDictionary<DisseminationNamespace, IDisseminationNamespace> _namespaces;

    public DisseminationProtocol(
        ILocalSiloDetails localSiloDetails,
        IInternalGrainFactory grainFactory,
        DisseminationMembership membership,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationNamespace> disseminationNamespaces,
        TimeProvider timeProvider,
        ILogger<DisseminationProtocol> logger)
    {
        // Capture the runtime collaborators and index namespaces once so receive-side lookups stay cheap.
        _localSilo = localSiloDetails.SiloAddress;
        _grainFactory = grainFactory;
        _membership = membership;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _namespaces = disseminationNamespaces.ToFrozenDictionary(static ns => ns.Name);

        // Let the queue own batching and direct peer delivery.
        _broadcastQueue = new DisseminationBroadcastQueue(
            timeProvider,
            _localSilo,
            grainFactory,
            _options,
            (peer, exception) => LogDebugDisseminationSendFailed(_logger, exception, peer),
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

        // Package the caller's value with local routing metadata before running publish-time validation.
        var item = CreateBroadcastValue(disseminationNamespace, value);
        if (!TryValidatePublishValue(disseminationNamespace, item, out var reason))
        {
            DisseminationInstruments.OnFallback(disseminationNamespace.Name, reason);
            return false;
        }

        // Use a membership view containing this silo so the initial broadcast tree is stable for the originator.
        var membership = await GetMembershipSnapshotForRouting(_localSilo, cancellationToken);
        if (membership is null)
        {
            return false;
        }

        // Remember the local publication and enqueue it for each direct tree target.
        var treeTargets = membership.OriginatorTreeTargets;
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

        // First apply all eligible values and collect only the new values which should continue through the tree.
        List<(IDisseminationNamespace Namespace, DisseminationBroadcastValue Item)>? pendingForwards = null;
        foreach (var (namespaceName, values) in batch.Values)
        {
            if (!TryGetEnabledNamespace(namespaceName, out var disseminationNamespace))
            {
                continue;
            }

            foreach (var item in values)
            {
                var applyResult = await ApplyReceivedValue(disseminationNamespace, item, batch.Sender, cancellationToken);
                if (applyResult is DisseminationApplyResult.Applied)
                {
                    (pendingForwards ??= []).Add((disseminationNamespace, item));
                }
            }
        }

        if (pendingForwards is not null)
        {
            // Then route successfully applied values to this node's forwarding targets, avoiding loops to sender/originator.
            foreach (var (disseminationNamespace, item) in pendingForwards)
            {
                var originator = item.Originator;
                var membership = await GetMembershipSnapshotForRouting(originator, cancellationToken);
                if (membership is null)
                {
                    continue;
                }

                foreach (var peer in membership.ForwardingTreeTargets)
                {
                    if (Equals(peer, originator) || Equals(peer, batch.Sender))
                    {
                        continue;
                    }

                    EnqueueBroadcast(peer, item, disseminationNamespace);
                }
            }
        }
    }

    public async Task RunAntiEntropyRound(CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        // Refresh transient peer state before selecting this round's anti-entropy exchange partners.
        var membership = _membership.CurrentSnapshot;
        await PrunePeerState(membership, cancellationToken);
        var options = _options.CurrentValue;
        var peers = membership.SelectAntiEntropyPeers(options.Overlay.AntiEntropyPeerCount);
        if (peers.IsDefaultOrEmpty)
        {
            return;
        }

        // Build the digest request once and skip the network fan-out when cadence suppression left nothing to ask for.
        var requestDigests = CreateAntiEntropyRequestDigests(_timeProvider.GetUtcNow());
        if (requestDigests.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Exchange digests with selected peers and apply any repair values they return.
        var request = new DisseminationAntiEntropyRequest { Digests = requestDigests };
        var tasks = new Task<DisseminationAntiEntropyResponse?>[peers.Length];
        for (var i = 0; i < peers.Length; i++)
        {
            tasks[i] = ExchangeAntiEntropyRequest(peers[i], request, cancellationToken);
        }

        var responses = await Task.WhenAll(tasks);
        await ApplyAntiEntropyResponses(responses, cancellationToken);
    }

    private Dictionary<DisseminationNamespace, List<DigestEntry>> CreateAntiEntropyRequestDigests(DateTimeOffset now)
    {
        // Build the anti-entropy request payload by grouping requested digests under their namespace.
        Dictionary<DisseminationNamespace, List<DigestEntry>>? digestsByNamespace = null;

        // Track every value stream which still exists locally so obsolete seen-value entries can be pruned later.
        var currentValueStreams = new HashSet<DigestKey>();

        // Walk each enabled namespace and decide which of its digests should be included in this round.
        foreach (var disseminationNamespace in _namespaces.Values)
        {
            if (!disseminationNamespace.Options.Enabled)
            {
                continue;
            }

            List<DigestEntry>? digestEntries = null;
            foreach (var digest in disseminationNamespace.Digests)
            {
                // Record the stream before applying cadence suppression so pruning only removes streams which disappeared.
                var digestKey = new DigestKey(disseminationNamespace.Name, digest.Key);
                currentValueStreams.Add(digestKey);

                // Request streams which have never been observed, or which have not updated within their expected cadence.
                bool shouldRequestDigest;
                lock (_seenValueLock)
                {
                    shouldRequestDigest = !_lastValueSeenAt.TryGetValue(digestKey, out var lastSeen)
                        || now - lastSeen >= disseminationNamespace.Options.ExpectedUpdateCadence;
                }

                if (shouldRequestDigest)
                {
                    (digestEntries ??= []).Add(digest);
                }
            }

            // Omit namespaces which do not have any digests to request in this round.
            if (digestEntries is null)
            {
                continue;
            }

            (digestsByNamespace ??= [])[disseminationNamespace.Name] = digestEntries;
        }

        // Remove seen-value timestamps for streams which are no longer reported by their namespace.
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

        return digestsByNamespace ?? [];
    }

    private async Task<DisseminationAntiEntropyResponse?> ExchangeAntiEntropyRequest(
        SiloAddress peer,
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer)
                .ExchangeAntiEntropy(request, cancellationToken);
            // Record exchange metrics after the peer returns so truncation and repair counts reflect the response.
            DisseminationInstruments.OnAntiEntropyExchange(
                "out",
                GetDigestCount(request.Digests),
                GetValueCount(response.Values),
                response.Truncated);
            return response;
        }
        catch (Exception exception)
        {
            // Anti-entropy transport failures are isolated to the peer; random peer selection naturally spreads retries.
            LogDebugDisseminationSendFailed(_logger, exception, peer);
            return null;
        }
    }

    private async Task ApplyAntiEntropyResponses(
        DisseminationAntiEntropyResponse?[] responses,
        CancellationToken cancellationToken)
    {
        // Visit each peer response independently; missing responses simply contributed no repair data.
        foreach (var response in responses)
        {
            if (response is null)
            {
                continue;
            }

            foreach (var (namespaceName, values) in response.Values)
            {
                // Ignore repair data for namespaces which are no longer configured or enabled locally.
                if (!TryGetEnabledNamespace(namespaceName, out var ns))
                {
                    continue;
                }

                foreach (var item in values)
                {
                    try
                    {
                        // Apply repairs through the same path as tree broadcasts without forwarding repaired values.
                        await ApplyReceivedValue(ns, item, response.Sender, cancellationToken);
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

    public ValueTask<DisseminationAntiEntropyResponse> ReceiveAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken)
    {
        // Keep the system-target API asynchronous while handling the CPU-only request synchronously.
        return new(ReceiveAntiEntropyCore(request));
    }

    private DisseminationAntiEntropyResponse ReceiveAntiEntropyCore(DisseminationAntiEntropyRequest request)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return new DisseminationAntiEntropyResponse
            {
                Sender = _localSilo,
                Values = [],
                Truncated = false,
            };
        }

        // Track request and response sizes so the reply respects configured batch limits and emits useful metrics.
        var valueCount = 0;
        var byteCount = 0;
        var truncated = false;
        var options = _options.CurrentValue;

        // Walk requested namespaces and compare the peer's versions with the local digest.
        var valuesByNamespace = new Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>>();
        foreach (var (namespaceName, remoteDigest) in request.Digests)
        {
            if (!TryGetEnabledNamespace(namespaceName, out var requestedNamespace))
            {
                continue;
            }

            if (remoteDigest.Count == 0)
            {
                continue;
            }

            var namespaceValues = new List<DisseminationBroadcastValue>();
            var remoteVersions = CreateDigestLookup(remoteDigest);
            foreach (var localDigest in requestedNamespace.Digests)
            {
                // Only repair streams requested by the peer and only when the local version is newer.
                if (!remoteVersions.TryGetValue(localDigest.Key, out var peerVersion))
                {
                    continue;
                }

                if (localDigest.Version <= peerVersion)
                {
                    continue;
                }

                // Ask the namespace to materialize the delta from the peer's version to the local version.
                if (!requestedNamespace.TryCreateRepairValue(
                    localDigest.Key,
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

                // Stop adding repair values once this response reaches the configured item or byte budget.
                if (valueCount >= options.MaxBatchItems || byteCount + item.Value.Payload.Length > options.MaxBatchBytes)
                {
                    truncated = true;
                    break;
                }

                namespaceValues.Add(item);
                ++valueCount;
                byteCount += item.Value.Payload.Length;
            }

            // Include the namespace result even when no repair values were produced, mirroring the request shape.
            valuesByNamespace[requestedNamespace.Name] = namespaceValues;
            if (truncated)
            {
                break;
            }
        }

        DisseminationInstruments.OnAntiEntropyExchange("in", GetDigestCount(request.Digests), valueCount, truncated);
        return new DisseminationAntiEntropyResponse
        {
            Sender = _localSilo,
            Values = valuesByNamespace,
            Truncated = truncated,
        };
    }

    private async ValueTask<DisseminationApplyResult> ApplyReceivedValue(
        IDisseminationNamespace disseminationNamespace,
        DisseminationBroadcastValue item,
        SiloAddress sender,
        CancellationToken cancellationToken)
    {
        // Reject oversized payloads before spending work on version checks or namespace application.
        var namespaceName = disseminationNamespace.Name;
        if (!ValidatePayloadSize(disseminationNamespace, item))
        {
            return DisseminationApplyResult.Rejected;
        }

        // Treat expired values as obsolete and emit the same observation path as other apply results.
        if (IsExpired(item))
        {
            EmitApplyResult(namespaceName, item, sender, DisseminationApplyResult.Obsolete);
            return DisseminationApplyResult.Obsolete;
        }

        // Short-circuit values whose versions prove they cannot be applied.
        if (GetApplicability(disseminationNamespace, item.Value) is { } applicabilityResult)
        {
            EmitApplyResult(namespaceName, item, sender, applicabilityResult);
            if (applicabilityResult == DisseminationApplyResult.Duplicate)
            {
                RecordSeenValue(namespaceName, item.Value.Key);
            }

            return applicabilityResult;
        }

        // Apply candidate values through the namespace, then remember streams which reached or matched local state.
        var result = await disseminationNamespace.ApplyValueAsync(item.Value, cancellationToken);
        EmitApplyResult(namespaceName, item, sender, result);
        if (result is DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate)
        {
            RecordSeenValue(namespaceName, item.Value.Key);
        }

        return result;
    }

    internal async Task FlushPendingBroadcast(CancellationToken cancellationToken)
    {
        // Expose the queue flush for tests and controlled shutdown paths without duplicating queue behavior here.
        await _broadcastQueue.FlushPendingBroadcast(cancellationToken);
    }

    internal async Task StopAsync(CancellationToken cancellationToken) =>
        await _broadcastQueue.StopAsync(cancellationToken);

    private void EnqueueBroadcast(SiloAddress peer, DisseminationBroadcastValue item, IDisseminationNamespace disseminationNamespace)
    {
        // Hand the item to the peer pump so it owns coalescing, scheduling, and send backoff.
        _broadcastQueue.Enqueue(peer, item, disseminationNamespace);
    }

    private void RecordSeenValue(DisseminationNamespace namespaceName, DisseminationKey key)
    {
        // Store the last local observation time for this stream so anti-entropy can suppress fresh digest requests.
        var digestKey = new DigestKey(namespaceName, key);
        lock (_seenValueLock)
        {
            _lastValueSeenAt[digestKey] = _timeProvider.GetUtcNow();
        }
    }

    private async Task PrunePeerState(DisseminationMembershipSnapshot membershipSnapshot, CancellationToken cancellationToken)
    {
        // Let the broadcast queue discard work for peers which are no longer valid routing targets.
        await _broadcastQueue.Prune(membershipSnapshot, cancellationToken);
    }

    private async ValueTask<DisseminationMembershipSnapshot?> GetMembershipSnapshotForRouting(
        SiloAddress originator,
        CancellationToken cancellationToken)
    {
        // Warn when local membership is stale before asking membership to refresh around the originator.
        if (!_membership.CurrentSnapshot.ContainsMember(originator))
        {
            LogDebugDisseminationOriginatorMissing(_logger, originator);
        }

        // Prune transient peer state against the freshest membership view available to this routing decision.
        var membership = await _membership.GetSnapshotContainingMember(originator, cancellationToken);
        await PrunePeerState(membership ?? _membership.CurrentSnapshot, cancellationToken);
        return membership;
    }

    private bool TryGetEnabledNamespace(DisseminationNamespace namespaceName, [NotNullWhen(true)] out IDisseminationNamespace? disseminationNamespace)
    {
        // Treat a namespace as usable only when dissemination and that namespace are both enabled.
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
        // Payloads must fit both their namespace cap and the transport batch cap.
        var options = _options.CurrentValue;
        if (item.Value.Payload.Length <= disseminationNamespace.Options.MaxPayloadBytes &&
            item.Value.Payload.Length <= options.MaxBatchBytes)
        {
            return true;
        }

        // Oversized values are dropped with both event and metrics signals for diagnosis.
        var namespaceName = disseminationNamespace.Name;
        DisseminationEvents.EmitPayloadDrop(namespaceName, item.Value, _localSilo, "oversize", item.Value.Payload.Length);
        DisseminationInstruments.OnPayloadDropped(namespaceName, "oversize");
        return false;
    }

    private bool TryValidatePublishValue(
        IDisseminationNamespace disseminationNamespace,
        DisseminationBroadcastValue item,
        [NotNullWhen(false)] out string? failureReason)
    {
        // Publications must still be live before they are allowed onto the broadcast tree.
        if (IsExpired(item))
        {
            failureReason = "expired";
            return false;
        }

        // Version ranges are validated before comparing them with local namespace state.
        if (!TryValidateVersionRange(item.Value, out failureReason))
        {
            return false;
        }

        // Avoid publishing values which cannot advance the namespace locally.
        if (disseminationNamespace.GetVersion(item.Value.Key) > item.Value.ToVersion)
        {
            failureReason = "obsolete";
            return false;
        }

        // Finally enforce size limits because that path also emits the drop diagnostics.
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
        // A value must describe a positive forward version range.
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
        // Invalid version ranges are rejected before consulting namespace state.
        if (!TryValidateVersionRange(value, out _))
        {
            return DisseminationApplyResult.Rejected;
        }

        // Compare the incoming range with the local version to distinguish old, duplicate, and candidate values.
        var localVersion = disseminationNamespace.GetVersion(value.Key);
        if (value.ToVersion < localVersion)
        {
            return DisseminationApplyResult.Obsolete;
        }

        if (value.ToVersion == localVersion)
        {
            return DisseminationApplyResult.Duplicate;
        }

        // Contiguous updates can be applied; gaps are rejected so state never skips a version range.
        return value.FromVersion == 0 || value.FromVersion == localVersion
            ? null
            : DisseminationApplyResult.Rejected;
    }

    private bool IsExpired(DisseminationBroadcastValue item)
    {
        // Treat values past their TTL as stale before applying or forwarding them.
        return item.ExpiresAt <= _timeProvider.GetUtcNow();
    }

    private DisseminationBroadcastValue CreateBroadcastValue(
        IDisseminationNamespace disseminationNamespace,
        DisseminationValue value)
    {
        // Attach origin and expiry metadata once so every outbound path shares the same broadcast envelope.
        return new()
        {
            Value = value,
            Originator = _localSilo,
            ExpiresAt = _timeProvider.GetUtcNow() + disseminationNamespace.Options.StaleItemTtl,
        };
    }

    private void EmitApplyResult(DisseminationNamespace namespaceName, DisseminationBroadcastValue item, SiloAddress sender, DisseminationApplyResult result)
    {
        // Publish both event and metric signals from one place so every apply path reports consistently.
        DisseminationEvents.EmitValue(namespaceName, item.Value, _localSilo, sender, result, item.Value.Payload.Length);
        DisseminationInstruments.OnValueApplied(namespaceName, result);
    }

    private static int GetDigestCount(Dictionary<DisseminationNamespace, List<DigestEntry>> digest)
    {
        // Sum per-namespace digest lists into the exchange-level count used by metrics.
        var result = 0;
        foreach (var entries in digest.Values)
        {
            result += entries.Count;
        }

        return result;
    }

    private static Dictionary<DisseminationKey, long> CreateDigestLookup(List<DigestEntry> digest)
    {
        // Convert the peer digest list into a version lookup keyed by value stream.
        var result = new Dictionary<DisseminationKey, long>(digest.Count);
        foreach (var entry in digest)
        {
            result[entry.Key] = entry.Version;
        }

        return result;
    }

    private static int GetValueCount(Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> valuesByNamespace)
    {
        // Sum per-namespace repair lists into the exchange-level count used by metrics.
        var result = 0;
        foreach (var values in valuesByNamespace.Values)
        {
            result += values.Count;
        }

        return result;
    }

    private readonly record struct DigestKey(DisseminationNamespace Namespace, DisseminationKey Key);

    // Generate the send-failure log method used by anti-entropy and broadcast peer pumps.
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination send to {Peer} failed.")]
    private static partial void LogDebugDisseminationSendFailed(ILogger logger, Exception exception, SiloAddress peer);

    // Generate the repair-failure log method used when an anti-entropy value cannot be applied.
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to apply anti-entropy repair value from {Sender} for namespace {Namespace}, key {Key}, version {Version}.")]
    private static partial void LogDebugAntiEntropyRepairValueFailed(ILogger logger, Exception exception, SiloAddress sender, DisseminationNamespace @namespace, DisseminationKey key, long version);

    // Generate the queue-flush log method used when a broadcast batch cannot be sent.
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination broadcast batch flush failed.")]
    private static partial void LogDebugBroadcastFlushFailed(ILogger logger, Exception exception);

    // Generate the originator-missing log method used before refreshing membership for routing.
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination originator {Originator} is missing from local membership; refreshing membership before routing.")]
    private static partial void LogDebugDisseminationOriginatorMissing(ILogger logger, SiloAddress originator);
}

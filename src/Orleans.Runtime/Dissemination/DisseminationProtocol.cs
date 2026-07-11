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
    private readonly object _valueUpdateLock = new();
    private readonly Dictionary<DigestKey, ValueUpdate> _lastValueUpdates = [];
    private readonly FrozenDictionary<DisseminationNamespace, IDisseminationNamespace> _namespaces;

    public DisseminationProtocol(
        ILocalSiloDetails localSiloDetails,
        IInternalGrainFactory grainFactory,
        DisseminationMembership membership,
        IOptionsMonitor<DisseminationOptions> options,
        IEnumerable<IDisseminationNamespace> disseminationNamespaces,
        TimeProvider timeProvider,
        ILogger<DisseminationProtocol> logger,
        ILogger<DisseminationBroadcastQueue> broadcastQueueLogger)
    {
        _localSilo = localSiloDetails.SiloAddress;
        _grainFactory = grainFactory;
        _membership = membership;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _namespaces = disseminationNamespaces.ToFrozenDictionary(static ns => ns.Name);

        _broadcastQueue = new DisseminationBroadcastQueue(
            _timeProvider,
            _localSilo,
            _grainFactory,
            _options,
            _namespaces.Values,
            broadcastQueueLogger);
    }

    public async ValueTask<bool> Publish(
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key,
        long version,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled || !disseminationNamespace.Options.Enabled)
        {
            return false;
        }

        if (!TryValidatePublish(
            disseminationNamespace,
            key,
            version,
            options,
            out var publishedVersion,
            out var reason))
        {
            DisseminationInstruments.OnFallback(disseminationNamespace.Name, reason);
            return false;
        }

        var membership = await GetMembershipSnapshotForRouting(_localSilo, cancellationToken);
        if (membership is null)
        {
            return false;
        }

        RecordValueUpdate(disseminationNamespace.Name, key, publishedVersion);
        foreach (var peer in membership.OriginatorTreeTargets)
        {
            _broadcastQueue.Notify(peer, disseminationNamespace, key);
        }

        return true;
    }

    public async Task<DisseminationBroadcastResponse> ReceiveBroadcast(
        DisseminationBroadcastBatch batch,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return new DisseminationBroadcastResponse
            {
                UnsupportedNamespaces = [.. batch.Values.Keys],
            };
        }

        DisseminationInstruments.OnBroadcastReceived(batch.Values, "tree");

        var receivedKeys = new Dictionary<IDisseminationNamespace, HashSet<DisseminationKey>>();
        var unsupportedNamespaces = new List<DisseminationNamespace>();
        foreach (var (namespaceName, values) in batch.Values)
        {
            if (!TryGetEnabledNamespace(namespaceName, out var disseminationNamespace))
            {
                unsupportedNamespaces.Add(namespaceName);
                continue;
            }

            var namespaceKeys = new HashSet<DisseminationKey>();
            receivedKeys.Add(disseminationNamespace, namespaceKeys);
            foreach (var item in values)
            {
                namespaceKeys.Add(item.Value.Key);
                _broadcastQueue.ObservePeerVersion(
                    batch.Sender,
                    namespaceName,
                    item.Value.Key,
                    item.Value.ToVersion);
                await ApplyReceivedValue(
                    disseminationNamespace,
                    item,
                    batch.Sender,
                    options,
                    cancellationToken);
            }
        }

        // Apply every value before choosing a routing snapshot because membership can be disseminated in the batch.
        var membership = _membership.CurrentSnapshot;
        await _broadcastQueue.Prune(membership, cancellationToken);
        foreach (var (disseminationNamespace, keys) in receivedKeys)
        {
            foreach (var key in keys)
            {
                if (disseminationNamespace.GetVersion(key) <= 0)
                {
                    continue;
                }

                foreach (var peer in membership.ForwardingTreeTargets)
                {
                    if (!Equals(peer, batch.Sender))
                    {
                        _broadcastQueue.Notify(peer, disseminationNamespace, key);
                    }
                }
            }
        }

        var acknowledgments = new Dictionary<DisseminationNamespace, List<DigestEntry>>();
        foreach (var (disseminationNamespace, keys) in receivedKeys)
        {
            var namespaceAcknowledgments = new List<DigestEntry>(keys.Count);
            foreach (var key in keys)
            {
                namespaceAcknowledgments.Add(new DigestEntry(key, disseminationNamespace.GetVersion(key)));
            }

            acknowledgments.Add(disseminationNamespace.Name, namespaceAcknowledgments);
        }

        return new DisseminationBroadcastResponse
        {
            Acknowledgments = acknowledgments,
            UnsupportedNamespaces = unsupportedNamespaces,
        };
    }

    public async Task RunAntiEntropyRound(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return;
        }

        var membership = _membership.CurrentSnapshot;
        await _broadcastQueue.Prune(membership, cancellationToken);
        var peers = membership.SelectAntiEntropyPeers(options.Overlay.AntiEntropyPeerCount);
        if (peers.IsDefaultOrEmpty)
        {
            return;
        }

        var requestDigests = CreateAntiEntropyRequestDigests(_timeProvider.GetTimestamp());
        if (requestDigests.Count == 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var request = new DisseminationAntiEntropyRequest
        {
            Sender = _localSilo,
            Digests = requestDigests,
        };
        var requestDigestCount = GetDigestCount(requestDigests);
        var responses = await Task.WhenAll(peers.Select(
            peer => ExchangeAntiEntropyRequest(peer, request, requestDigestCount, cancellationToken)));
        await ApplyAntiEntropyResponses(responses, options, cancellationToken);
    }

    private Dictionary<DisseminationNamespace, List<DigestEntry>> CreateAntiEntropyRequestDigests(long now)
    {
        Dictionary<DigestKey, ValueUpdate> lastValueUpdates;
        lock (_valueUpdateLock)
        {
            lastValueUpdates = new(_lastValueUpdates);
        }

        Dictionary<DisseminationNamespace, List<DigestEntry>>? digestsByNamespace = null;
        var currentValueStreams = new HashSet<DigestKey>();

        foreach (var disseminationNamespace in _namespaces.Values)
        {
            var namespaceOptions = disseminationNamespace.Options;
            if (!namespaceOptions.Enabled)
            {
                continue;
            }

            List<DigestEntry>? digestEntries = null;
            foreach (var digest in disseminationNamespace.Digests)
            {
                var digestKey = new DigestKey(disseminationNamespace.Name, digest.Key);
                currentValueStreams.Add(digestKey);

                if (!lastValueUpdates.TryGetValue(digestKey, out var lastUpdate)
                    || lastUpdate.Version != digest.Version
                    || _timeProvider.GetElapsedTime(lastUpdate.Timestamp, now) >= namespaceOptions.ExpectedUpdateCadence)
                {
                    (digestEntries ??= []).Add(digest);
                }
            }

            if (digestEntries is not null)
            {
                (digestsByNamespace ??= [])[disseminationNamespace.Name] = digestEntries;
            }
        }

        lock (_valueUpdateLock)
        {
            List<DigestKey>? removedKeys = null;
            foreach (var key in _lastValueUpdates.Keys)
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
                    _lastValueUpdates.Remove(key);
                }
            }
        }

        return digestsByNamespace ?? [];
    }

    private async Task<DisseminationAntiEntropyResponse?> ExchangeAntiEntropyRequest(
        SiloAddress peer,
        DisseminationAntiEntropyRequest request,
        int requestDigestCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer)
                .ExchangeAntiEntropy(request, cancellationToken);
            // Record exchange metrics after the peer returns so truncation and repair counts reflect the response.
            DisseminationInstruments.OnAntiEntropyExchange(
                "out",
                requestDigestCount,
                GetValueCount(response.Values),
                response.Truncated);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        DisseminationOptions options,
        CancellationToken cancellationToken)
    {
        Dictionary<DigestKey, List<AntiEntropyRepair>>? repairs = null;
        foreach (var response in responses)
        {
            if (response is null)
            {
                continue;
            }

            foreach (var (namespaceName, values) in response.Values)
            {
                if (!TryGetEnabledNamespace(namespaceName, out var disseminationNamespace))
                {
                    continue;
                }

                foreach (var stream in values.GroupBy(static item => item.Value.Key))
                {
                    repairs ??= [];
                    var items = stream.ToList();
                    var key = new DigestKey(namespaceName, stream.Key);
                    if (!repairs.TryGetValue(key, out var candidates))
                    {
                        candidates = [];
                        repairs.Add(key, candidates);
                    }

                    var terminalVersion = items.Max(static item => item.Value.ToVersion);
                    _broadcastQueue.ObservePeerVersion(
                        response.Sender,
                        namespaceName,
                        stream.Key,
                        terminalVersion);
                    candidates.Add(new(disseminationNamespace, items, response.Sender));
                }
            }
        }

        if (repairs is null)
        {
            return;
        }

        foreach (var candidates in repairs.Values)
        {
            candidates.Sort(CompareAntiEntropyRepairs);

            foreach (var candidate in candidates)
            {
                foreach (var item in candidate.Items)
                {
                    await ApplyReceivedValue(
                        candidate.Namespace,
                        item,
                        candidate.Sender,
                        options,
                        cancellationToken);
                }
            }
        }
    }

    public ValueTask<DisseminationAntiEntropyResponse> ReceiveAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (namespaceName, entries) in request.Digests)
        {
            foreach (var entry in entries)
            {
                _broadcastQueue.ObservePeerVersion(
                    request.Sender,
                    namespaceName,
                    entry.Key,
                    entry.Version);
            }
        }

        var options = _options.CurrentValue;
        return new(CreateAntiEntropyResponse(request, options, cancellationToken));
    }

    private DisseminationAntiEntropyResponse CreateAntiEntropyResponse(
        DisseminationAntiEntropyRequest request,
        DisseminationOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return new DisseminationAntiEntropyResponse
            {
                Sender = _localSilo,
                Values = [],
                Truncated = false,
            };
        }

        var valueCount = 0;
        var byteCount = 0;
        var truncated = false;
        var valuesByNamespace = new Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>>();
        foreach (var (namespaceName, remoteDigest) in request.Digests)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken.ThrowIfCancellationRequested();
                if (!remoteVersions.TryGetValue(localDigest.Key, out var peerDigest))
                {
                    continue;
                }

                if (localDigest.Version < peerDigest.Version
                    || localDigest.Version == peerDigest.Version
                    && localDigest.Fingerprint == peerDigest.Fingerprint)
                {
                    continue;
                }

                var repairRequest = new DisseminationRepairRequest(
                    localDigest.Key,
                    peerDigest.Version,
                    toVersion: null,
                    options.MaxBatchItems - valueCount,
                    options.MaxBatchBytes - byteCount,
                    requestedNamespace.Options.MaxPayloadBytes);
                var repair = requestedNamespace.CreateRepair(repairRequest);
                if (repair.Status is DisseminationRepairStatus.InsufficientCapacity)
                {
                    if (valueCount > 0)
                    {
                        var emptyBatchRequest = new DisseminationRepairRequest(
                            localDigest.Key,
                            peerDigest.Version,
                            toVersion: null,
                            options.MaxBatchItems,
                            options.MaxBatchBytes,
                            requestedNamespace.Options.MaxPayloadBytes);
                        var emptyBatchRepair = requestedNamespace.CreateRepair(emptyBatchRequest);
                        if (emptyBatchRepair.Status is DisseminationRepairStatus.Produced
                            && ValidateRepair(
                                requestedNamespace,
                                emptyBatchRequest,
                                emptyBatchRepair,
                                options))
                        {
                            truncated = true;
                            break;
                        }
                    }

                    continue;
                }

                if (repair.Status is not DisseminationRepairStatus.Produced
                    || !ValidateRepair(requestedNamespace, repairRequest, repair, options))
                {
                    continue;
                }

                foreach (var value in repair.Values)
                {
                    namespaceValues.Add(CreateBroadcastValue(requestedNamespace, value));
                    ++valueCount;
                    byteCount += value.Payload.Length;
                }

                if (!repair.IsComplete)
                {
                    truncated = true;
                    break;
                }
            }

            if (namespaceValues.Count > 0)
            {
                valuesByNamespace[requestedNamespace.Name] = namespaceValues;
            }

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
        DisseminationOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyReceivedValueCore(
                disseminationNamespace,
                item,
                sender,
                options,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogDebugDisseminationValueApplyFailed(
                _logger,
                exception,
                sender,
                disseminationNamespace.Name,
                item.Value.Key,
                item.Value.ToVersion);
            EmitApplyResult(disseminationNamespace.Name, item, sender, DisseminationApplyResult.Rejected);
            return DisseminationApplyResult.Rejected;
        }
    }

    private async ValueTask<DisseminationApplyResult> ApplyReceivedValueCore(
        IDisseminationNamespace disseminationNamespace,
        DisseminationBroadcastValue item,
        SiloAddress sender,
        DisseminationOptions options,
        CancellationToken cancellationToken)
    {
        var namespaceName = disseminationNamespace.Name;
        if (!ValidatePayloadSize(disseminationNamespace, item.Value, options))
        {
            return DisseminationApplyResult.Rejected;
        }

        if (IsExpired(item))
        {
            EmitApplyResult(namespaceName, item, sender, DisseminationApplyResult.Obsolete);
            return DisseminationApplyResult.Obsolete;
        }

        if (TryGetTerminalApplyResult(disseminationNamespace, item.Value, out var terminalResult))
        {
            EmitApplyResult(namespaceName, item, sender, terminalResult);
            return terminalResult;
        }

        var result = await disseminationNamespace.ApplyValueAsync(item.Value, cancellationToken);
        EmitApplyResult(namespaceName, item, sender, result);
        if (result is DisseminationApplyResult.Applied)
        {
            RecordValueUpdate(namespaceName, item.Value.Key, item.Value.ToVersion);
        }

        return result;
    }

    internal Task FlushPendingBroadcast(CancellationToken cancellationToken) =>
        _broadcastQueue.FlushPendingBroadcast(cancellationToken);

    internal Task StopAsync(CancellationToken cancellationToken) =>
        _broadcastQueue.StopAsync(cancellationToken);

    private void RecordValueUpdate(DisseminationNamespace namespaceName, DisseminationKey key, long version)
    {
        var digestKey = new DigestKey(namespaceName, key);
        var update = new ValueUpdate(version, _timeProvider.GetTimestamp());
        lock (_valueUpdateLock)
        {
            if (!_lastValueUpdates.TryGetValue(digestKey, out var previous) || version > previous.Version)
            {
                _lastValueUpdates[digestKey] = update;
            }
        }
    }

    private async ValueTask<DisseminationMembershipSnapshot?> GetMembershipSnapshotForRouting(
        SiloAddress member,
        CancellationToken cancellationToken)
    {
        var membership = await _membership.GetSnapshotContainingMember(member, cancellationToken);
        await _broadcastQueue.Prune(membership ?? _membership.CurrentSnapshot, cancellationToken);
        return membership;
    }

    private bool TryGetEnabledNamespace(DisseminationNamespace namespaceName, [NotNullWhen(true)] out IDisseminationNamespace? disseminationNamespace)
    {
        if (_namespaces.TryGetValue(namespaceName, out disseminationNamespace)
            && disseminationNamespace.Options.Enabled)
        {
            return true;
        }

        disseminationNamespace = null;
        return false;
    }

    private bool ValidatePayloadSize(
        IDisseminationNamespace disseminationNamespace,
        DisseminationValue value,
        DisseminationOptions options)
    {
        if (value.Payload.Length <= disseminationNamespace.Options.MaxPayloadBytes
            && value.Payload.Length <= options.MaxBatchBytes)
        {
            return true;
        }

        var namespaceName = disseminationNamespace.Name;
        DisseminationEvents.EmitPayloadDrop(namespaceName, value, _localSilo, "oversize", value.Payload.Length);
        DisseminationInstruments.OnPayloadDropped(namespaceName, "oversize");
        return false;
    }

    private bool TryValidatePublish(
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key,
        long version,
        DisseminationOptions options,
        out long publishedVersion,
        [NotNullWhen(false)] out string? failureReason)
    {
        if (version <= 0)
        {
            publishedVersion = 0;
            failureReason = "invalid-version";
            return false;
        }

        if (disseminationNamespace.GetVersion(key) < version)
        {
            publishedVersion = 0;
            failureReason = "unavailable";
            return false;
        }

        var request = new DisseminationRepairRequest(
            key,
            fromVersion: null,
            toVersion: null,
            maxItemCount: int.MaxValue,
            maxBatchBytes: int.MaxValue,
            disseminationNamespace.Options.MaxPayloadBytes);
        var repair = disseminationNamespace.CreateRepair(request);
        if (repair.Status is DisseminationRepairStatus.InsufficientCapacity)
        {
            publishedVersion = 0;
            failureReason = "oversize";
            return false;
        }

        if (repair.Status is not DisseminationRepairStatus.Produced
            || !repair.IsComplete
            || repair.Version < version
            || !ValidateRepair(disseminationNamespace, request, repair, options))
        {
            publishedVersion = 0;
            failureReason = "invalid-repair";
            return false;
        }

        publishedVersion = repair.Version;
        failureReason = null;
        return true;
    }

    private bool ValidateRepair(
        IDisseminationNamespace disseminationNamespace,
        in DisseminationRepairRequest request,
        in DisseminationRepairResult repair,
        DisseminationOptions options)
    {
        if (repair.Status is not DisseminationRepairStatus.Produced
            || repair.Values.IsDefaultOrEmpty
            || repair.Version <= 0
            || request.ToVersion is { } requestedToVersion && repair.Version > requestedToVersion
            || repair.Values.Length > request.MaxItemCount)
        {
            return false;
        }

        var byteCount = 0;
        var expectedFromVersion = request.FromVersion;
        foreach (var value in repair.Values)
        {
            if (value.Key != request.Key
                || !IsValidVersionRange(value)
                || value.ToVersion > repair.Version
                || !ValidatePayloadSize(disseminationNamespace, value, options))
            {
                return false;
            }

            if (expectedFromVersion is null && value.FromVersion != 0
                || expectedFromVersion is { } fromVersion
                && value.FromVersion != 0
                && value.FromVersion != fromVersion)
            {
                return false;
            }

            expectedFromVersion = value.ToVersion;
            byteCount += value.Payload.Length;
            if (byteCount > request.MaxBatchBytes)
            {
                return false;
            }
        }

        var lastVersion = repair.Values[^1].ToVersion;
        return repair.IsComplete ? lastVersion == repair.Version : lastVersion < repair.Version;
    }

    private static bool IsValidVersionRange(DisseminationValue value) =>
        value is { FromVersion: >= 0, ToVersion: > 0 } && value.ToVersion > value.FromVersion;

    private static bool TryGetTerminalApplyResult(
        IDisseminationNamespace disseminationNamespace,
        DisseminationValue value,
        out DisseminationApplyResult result)
    {
        if (!IsValidVersionRange(value))
        {
            result = DisseminationApplyResult.Rejected;
            return true;
        }

        var localVersion = disseminationNamespace.GetVersion(value.Key);
        if (value.ToVersion < localVersion)
        {
            result = DisseminationApplyResult.Obsolete;
            return true;
        }

        if (value.ToVersion == localVersion || value.FromVersion == 0 || value.FromVersion == localVersion)
        {
            result = default;
            return false;
        }

        result = DisseminationApplyResult.Rejected;
        return true;
    }

    private bool IsExpired(DisseminationBroadcastValue item) =>
        item.ExpiresAt <= _timeProvider.GetUtcNow();

    private DisseminationBroadcastValue CreateBroadcastValue(
        IDisseminationNamespace disseminationNamespace,
        DisseminationValue value) =>
        new()
        {
            Value = value,
            ExpiresAt = _timeProvider.GetUtcNow() + disseminationNamespace.Options.StaleItemTtl,
        };

    private void EmitApplyResult(DisseminationNamespace namespaceName, DisseminationBroadcastValue item, SiloAddress sender, DisseminationApplyResult result)
    {
        DisseminationEvents.EmitValue(namespaceName, item.Value, _localSilo, sender, result, item.Value.Payload.Length);
        DisseminationInstruments.OnValueApplied(namespaceName, result);
    }

    private static int GetDigestCount(Dictionary<DisseminationNamespace, List<DigestEntry>> digest) => digest.Values.Sum(entries => entries.Count);

    private static Dictionary<DisseminationKey, DigestEntry> CreateDigestLookup(List<DigestEntry> digest)
    {
        var result = new Dictionary<DisseminationKey, DigestEntry>(digest.Count);
        foreach (var entry in digest)
        {
            result[entry.Key] = entry;
        }

        return result;
    }

    private static int GetValueCount(Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> valuesByNamespace) => valuesByNamespace.Values.Sum(values => values.Count);

    private static int CompareAntiEntropyRepairs(AntiEntropyRepair left, AntiEntropyRepair right)
    {
        var result = right.Items[^1].Value.ToVersion.CompareTo(left.Items[^1].Value.ToVersion);
        if (result != 0)
        {
            return result;
        }

        var leftIsFullValue = left.Items[0].Value.FromVersion == 0;
        var rightIsFullValue = right.Items[0].Value.FromVersion == 0;
        if (leftIsFullValue != rightIsFullValue)
        {
            return leftIsFullValue ? -1 : 1;
        }

        return left.Sender.CompareTo(right.Sender);
    }

    private readonly record struct DigestKey(DisseminationNamespace Namespace, DisseminationKey Key);

    private readonly record struct ValueUpdate(long Version, long Timestamp);

    private readonly record struct AntiEntropyRepair(
        IDisseminationNamespace Namespace,
        List<DisseminationBroadcastValue> Items,
        SiloAddress Sender);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination send to {Peer} failed.")]
    private static partial void LogDebugDisseminationSendFailed(ILogger logger, Exception exception, SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to apply dissemination value from {Sender} for namespace {Namespace}, key {Key}, version {Version}.")]
    private static partial void LogDebugDisseminationValueApplyFailed(ILogger logger, Exception exception, SiloAddress sender, DisseminationNamespace @namespace, DisseminationKey key, long version);

}

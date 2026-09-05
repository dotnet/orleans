using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

// The protocol coordinates routing and application while namespaces remain authoritative for values and repair history.
internal sealed partial class DisseminationProtocol
{
    private const int MaxRetainedNonMemberResponseCursors = 64;
    private static readonly TimeSpan MaxAntiEntropyRoundLifetime = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private readonly SiloAddress _localSilo;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly DisseminationMembership _membership;
    private readonly IOptionsMonitor<DisseminationOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DisseminationProtocol> _logger;
    private readonly DisseminationBroadcastQueue _broadcastQueue;
    private readonly object _antiEntropyResponseCursorLock = new();
    private readonly Dictionary<SiloAddress, AntiEntropyResponseCursor> _antiEntropyResponseCursors = [];
    private long _antiEntropyResponseCursorAccess;
    private readonly object _valueUpdateLock = new();
    private readonly Dictionary<DigestKey, ValueUpdate> _lastValueUpdates = [];
    private readonly object _peerSupportLock = new();
    private readonly Dictionary<SiloAddress, Dictionary<DisseminationNamespace, long>> _confirmedPeerNamespaces = [];
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
            broadcastQueueLogger,
            ObserveBroadcastResponse);
    }

    public async ValueTask<bool> Publish(
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key,
        long version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = _options.CurrentValue;
        if (!options.Enabled || !disseminationNamespace.Options.Enabled)
        {
            DisseminationInstruments.OnPublication(
                disseminationNamespace.Name,
                accepted: false,
                reason: "disabled");
            return false;
        }

        // Before replacing the legacy fallback, prove that an unknown peer can receive a complete, bounded repair.
        if (!TryValidatePublish(
            disseminationNamespace,
            key,
            version,
            options,
            out var publishedVersion,
            out var reason))
        {
            DisseminationInstruments.OnPublication(
                disseminationNamespace.Name,
                accepted: false,
                reason: reason);
            return false;
        }

        var membership = await GetMembershipSnapshotForRouting(
            disseminationNamespace.MembershipScope,
            _localSilo,
            cancellationToken);
        if (membership is null)
        {
            DisseminationInstruments.OnPublication(
                disseminationNamespace.Name,
                accepted: false,
                "membership-unavailable");
            return false;
        }

        // Notifications carry identity only; each peer pump asks the namespace for the latest repair at send time.
        RecordValueUpdate(disseminationNamespace.Name, key, publishedVersion);
        var accepted = true;
        foreach (var peer in membership.OriginatorTreeTargets)
        {
            accepted &= _broadcastQueue.Notify(peer, disseminationNamespace, key);
        }

        DisseminationInstruments.OnPublication(
            disseminationNamespace.Name,
            accepted,
            reason: accepted ? "none" : "queue-rejected");
        return accepted;
    }

    public async Task<DisseminationBroadcastResponse> ReceiveBroadcast(
        DisseminationBroadcastBatch batch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return new DisseminationBroadcastResponse
            {
                UnsupportedNamespaces = [.. batch.Values.Keys],
            };
        }

        var receivedKeys = new Dictionary<IDisseminationNamespace, Dictionary<DisseminationKey, bool>>();
        var unsupportedNamespaces = new List<DisseminationNamespace>();
        foreach (var (namespaceName, values) in batch.Values)
        {
            if (!TryGetEnabledNamespace(namespaceName, out var disseminationNamespace))
            {
                unsupportedNamespaces.Add(namespaceName);
                continue;
            }

            DisseminationInstruments.OnBroadcastReceived(disseminationNamespace.Name, "tree", values.Count);
            ConfirmPeerNamespaces(batch.Sender, [namespaceName]);
            var namespaceKeys = new Dictionary<DisseminationKey, bool>();
            receivedKeys.Add(disseminationNamespace, namespaceKeys);
            foreach (var item in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                namespaceKeys.TryAdd(item.Value.Key, false);
                // The sender necessarily owns this version; use that fact only if an outbound ledger already exists.
                _broadcastQueue.ObservePeerVersion(
                    batch.Sender,
                    namespaceName,
                    item.Value.Key,
                    item.Value.ToVersion);
                var result = await ApplyReceivedValue(
                    disseminationNamespace,
                    item,
                    batch.Sender,
                    options,
                    cancellationToken);
                if (result is DisseminationApplyResult.Applied)
                {
                    namespaceKeys[item.Value.Key] = true;
                }
            }
        }

        // Membership may be part of this batch, so apply everything before deriving the forwarding tree.
        var membershipSnapshots = _membership.CurrentSnapshots;
        PrunePeerNamespaceConfirmations(membershipSnapshots.AllMembers);
        await _broadcastQueue.Prune(membershipSnapshots, cancellationToken);
        // Changed state always wakes children, including same-version liveness updates. Duplicate deliveries
        // wake only children which still need this version and have no equivalent queued work.
        foreach (var (disseminationNamespace, keys) in receivedKeys)
        {
            var membership = membershipSnapshots.GetSnapshot(disseminationNamespace.MembershipScope);
            foreach (var (key, applied) in keys)
            {
                if (disseminationNamespace.GetVersion(key) <= 0)
                {
                    continue;
                }

                foreach (var peer in membership.ForwardingTreeTargets)
                {
                    if (!Equals(peer, batch.Sender))
                    {
                        _broadcastQueue.Notify(peer, disseminationNamespace, key, force: applied);
                    }
                }
            }
        }

        // Once downstream work is queued, report the versions this receiver actually holds.
        var acknowledgments = new Dictionary<DisseminationNamespace, List<DigestEntry>>();
        foreach (var (disseminationNamespace, keys) in receivedKeys)
        {
            var namespaceAcknowledgments = new List<DigestEntry>(keys.Count);
            foreach (var key in keys.Keys)
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
        cancellationToken.ThrowIfCancellationRequested();
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return;
        }

        var membershipSnapshots = _membership.CurrentSnapshots;
        PrunePeerNamespaceConfirmations(membershipSnapshots.AllMembers);
        await _broadcastQueue.Prune(membershipSnapshots, cancellationToken);

        // Push traffic suppresses redundant checks; quiet streams are offered to a few random peers.
        var requestDigests = CreateAntiEntropyRequestDigests(_timeProvider.GetTimestamp());
        cancellationToken.ThrowIfCancellationRequested();

        var requests = CreateAntiEntropyRequests(
            membershipSnapshots,
            requestDigests,
            options.Overlay.AntiEntropyPeerCount);
        if (requests.Count == 0)
        {
            return;
        }

        var roundLifetime = GetAntiEntropyRoundLifetime(requests.Values);
        using var lifetimeCancellation = new CancellationTokenSource(roundLifetime, _timeProvider);
        using var exchangeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        var responseTasks = requests
            .Select(request => ExchangeAntiEntropyRequest(
                request.Key,
                request.Value,
                GetDigestCount(request.Value.Digests),
                exchangeCancellation.Token,
                cancellationToken,
                lifetimeCancellation.Token))
            .ToArray();
        DisseminationAntiEntropyResponse?[] responses;
        try
        {
            responses = await Task.WhenAll(responseTasks)
                .WaitAsync(roundLifetime, _timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            await lifetimeCancellation.CancelAsync();
            responses = responseTasks
                .Where(static response => response.IsCompletedSuccessfully)
                .Select(static response => response.Result)
                .ToArray();
        }

        cancellationToken.ThrowIfCancellationRequested();
        await ApplyAntiEntropyResponses(responses, options, cancellationToken);
    }

    private Dictionary<SiloAddress, DisseminationAntiEntropyRequest> CreateAntiEntropyRequests(
        DisseminationMembershipSnapshots membershipSnapshots,
        Dictionary<DisseminationNamespace, List<DigestEntry>> requestDigests,
        int peerCount)
    {
        var result = new Dictionary<SiloAddress, DisseminationAntiEntropyRequest>();
        var namespacesByScope = new Dictionary<DisseminationMembershipScope, IDisseminationNamespace[]>();
        foreach (var scope in Enum.GetValues<DisseminationMembershipScope>())
        {
            var scopedNamespaces = _namespaces.Values
                .Where(disseminationNamespace => disseminationNamespace.Options.Enabled
                    && disseminationNamespace.MembershipScope == scope)
                .ToArray();
            if (scopedNamespaces.Length > 0)
            {
                namespacesByScope.Add(scope, scopedNamespaces);
            }
        }

        if (namespacesByScope.Count == 0)
        {
            return result;
        }

        // AllMembers is a superset of ActiveMembers. Selecting from the broadest participating
        // projection preserves one global per-round peer budget while still attaching only the
        // namespaces for which each selected peer is eligible.
        var selectionScope = namespacesByScope.ContainsKey(DisseminationMembershipScope.AllMembers)
            ? DisseminationMembershipScope.AllMembers
            : DisseminationMembershipScope.ActiveMembers;
        foreach (var peer in membershipSnapshots.GetSnapshot(selectionScope).SelectAntiEntropyPeers(peerCount))
        {
            var peerDigests = new Dictionary<DisseminationNamespace, List<DigestEntry>>();
            var supportedNamespaces = new List<DisseminationNamespace>();
            foreach (var (scope, scopedNamespaces) in namespacesByScope)
            {
                if (!membershipSnapshots.GetSnapshot(scope).ContainsMember(peer))
                {
                    continue;
                }

                foreach (var disseminationNamespace in scopedNamespaces)
                {
                    supportedNamespaces.Add(disseminationNamespace.Name);
                    if (requestDigests.TryGetValue(disseminationNamespace.Name, out var digest))
                    {
                        peerDigests.Add(disseminationNamespace.Name, digest);
                    }
                }
            }

            if (supportedNamespaces.Count > 0)
            {
                result.Add(peer, new DisseminationAntiEntropyRequest
                {
                    Sender = _localSilo,
                    Digests = peerDigests,
                    SupportedNamespaces = supportedNamespaces,
                });
            }
        }

        return result;
    }

    private TimeSpan GetAntiEntropyRoundLifetime(
        IEnumerable<DisseminationAntiEntropyRequest> requests)
    {
        var result = MaxAntiEntropyRoundLifetime;
        foreach (var request in requests)
        {
            foreach (var namespaceName in request.SupportedNamespaces)
            {
                if (_namespaces.TryGetValue(namespaceName, out var disseminationNamespace)
                    && disseminationNamespace.Options.StaleItemTtl < result)
                {
                    result = disseminationNamespace.Options.StaleItemTtl;
                }
            }
        }

        return result;
    }

    private Dictionary<DisseminationNamespace, List<DigestEntry>> CreateAntiEntropyRequestDigests(long now)
    {
        // New or quiet streams need periodic repair; this pass also forgets streams which disappeared.
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
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken,
        CancellationToken lifetimeCancellationToken)
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
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (lifetimeCancellationToken.IsCancellationRequested)
        {
            DisseminationInstruments.OnAntiEntropyFailure(DisseminationFailureReason.Timeout);
            return null;
        }
        catch (Exception exception)
        {
            // Anti-entropy transport failures are isolated to the peer; random peer selection naturally spreads retries.
            DisseminationInstruments.OnAntiEntropyFailure(DisseminationFailureReason.Error);
            LogDebugDisseminationSendFailed(_logger, exception, peer);
            return null;
        }
    }

    private async Task ApplyAntiEntropyResponses(
        DisseminationAntiEntropyResponse?[] responses,
        DisseminationOptions options,
        CancellationToken cancellationToken)
    {
        // Keep each sender's chain intact and rank all repairs which completed within the round's hop lifetime.
        Dictionary<DigestKey, List<AntiEntropyRepair>>? repairs = null;
        foreach (var response in responses)
        {
            if (response is null)
            {
                continue;
            }

            ConfirmPeerNamespaces(response.Sender, response.SupportedNamespaces);
            RevokePeerNamespaces(response.Sender, response.UnsupportedNamespaces);
            // A response only includes namespaces which produced repairs. Absence is not evidence that an
            // up-to-date or unrelated namespace is unsupported, so confirmations are additive here.
            ConfirmPeerNamespaces(response.Sender, response.Values.Keys);
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
            // Try the furthest-reaching repair first, preferring a full value when candidates tie.
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
        ConfirmPeerNamespaces(request.Sender, request.SupportedNamespaces);
        ConfirmPeerNamespaces(request.Sender, request.Digests.Keys);
        // Incoming digests are passive evidence for existing peer pumps, not a reason to create new ones.
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
        var response = CreateAntiEntropyResponse(request, options, cancellationToken);
        PruneAntiEntropyResponseCursors(_membership.CurrentSnapshots.AllMembers, request.Sender);
        return new(response);
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
                UnsupportedNamespaces = [.. request.SupportedNamespaces],
            };
        }

        var supportedNamespaces = new List<DisseminationNamespace>();
        var unsupportedNamespaces = new List<DisseminationNamespace>();
        foreach (var namespaceName in request.SupportedNamespaces)
        {
            if (TryGetEnabledNamespace(namespaceName, out _))
            {
                supportedNamespaces.Add(namespaceName);
            }
            else
            {
                unsupportedNamespaces.Add(namespaceName);
            }
        }

        var valueCount = 0;
        var byteCount = 0;
        var truncated = false;
        var valuesByNamespace = new Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>>();
        var candidates = new List<AntiEntropyResponseCandidate>();
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

                candidates.Add(new(requestedNamespace, localDigest, peerDigest));
            }
        }

        var start = GetAntiEntropyResponseStart(request.Sender, candidates.Count);
        var examined = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[(start + i) % candidates.Count];
            examined++;
            var requestedNamespace = candidate.Namespace;
            var localDigest = candidate.LocalDigest;
            var peerDigest = candidate.PeerDigest;
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
                    // Probe with a fresh batch budget to distinguish truncation from a permanently oversized key.
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
                        // Resume with this candidate next round because the current response budget, not the
                        // candidate itself, prevented it from being included.
                        examined--;
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

            if (!valuesByNamespace.TryGetValue(requestedNamespace.Name, out var namespaceValues))
            {
                namespaceValues = [];
                valuesByNamespace.Add(requestedNamespace.Name, namespaceValues);
            }

            foreach (var value in repair.Values)
            {
                namespaceValues.Add(CreateBroadcastValue(requestedNamespace, value));
                ++valueCount;
                byteCount += value.Payload.Length;
            }

            if (!repair.IsComplete)
            {
                // A valid prefix consumes this response; the caller can continue in its next round.
                truncated = true;
                break;
            }
        }

        if (truncated)
        {
            AdvanceAntiEntropyResponseCursor(request.Sender, start, examined, candidates.Count);
        }
        else
        {
            ClearAntiEntropyResponseCursor(request.Sender);
        }

        DisseminationInstruments.OnAntiEntropyExchange("in", GetDigestCount(request.Digests), valueCount, truncated);
        return new DisseminationAntiEntropyResponse
        {
            Sender = _localSilo,
            Values = valuesByNamespace,
            Truncated = truncated,
            SupportedNamespaces = supportedNamespaces,
            UnsupportedNamespaces = unsupportedNamespaces,
        };
    }

    private int GetAntiEntropyResponseStart(SiloAddress peer, int candidateCount)
    {
        if (candidateCount == 0)
        {
            return 0;
        }

        lock (_antiEntropyResponseCursorLock)
        {
            if (!_antiEntropyResponseCursors.TryGetValue(peer, out var cursor))
            {
                return 0;
            }

            _antiEntropyResponseCursors[peer] = cursor with
            {
                LastAccess = ++_antiEntropyResponseCursorAccess,
            };
            return cursor.Position % candidateCount;
        }
    }

    private void AdvanceAntiEntropyResponseCursor(
        SiloAddress peer,
        int start,
        int examined,
        int candidateCount)
    {
        lock (_antiEntropyResponseCursorLock)
        {
            if (candidateCount == 0)
            {
                _antiEntropyResponseCursors.Remove(peer);
            }
            else
            {
                _antiEntropyResponseCursors[peer] = new(
                    (start + Math.Max(1, examined)) % candidateCount,
                    ++_antiEntropyResponseCursorAccess);
            }
        }
    }

    private void ClearAntiEntropyResponseCursor(SiloAddress peer)
    {
        lock (_antiEntropyResponseCursorLock)
        {
            _antiEntropyResponseCursors.Remove(peer);
        }
    }

    private void PruneAntiEntropyResponseCursors(
        DisseminationMembershipSnapshot membership,
        SiloAddress currentRequester)
    {
        lock (_antiEntropyResponseCursorLock)
        {
            var nonMemberCount = 0;
            foreach (var peer in _antiEntropyResponseCursors.Keys)
            {
                if (!membership.ContainsMember(peer))
                {
                    nonMemberCount++;
                }
            }

            if (nonMemberCount <= MaxRetainedNonMemberResponseCursors)
            {
                return;
            }

            foreach (var cursor in _antiEntropyResponseCursors
                .Where(entry => !Equals(entry.Key, currentRequester) && !membership.ContainsMember(entry.Key))
                .OrderBy(static entry => entry.Value.LastAccess)
                .ToArray())
            {
                _antiEntropyResponseCursors.Remove(cursor.Key);
                if (--nonMemberCount <= MaxRetainedNonMemberResponseCursors)
                {
                    break;
                }
            }
        }
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
        cancellationToken.ThrowIfCancellationRequested();
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

        // Reject gaps before deserializing; full values from version zero can replace any baseline.
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

    internal IReadOnlyList<SiloAddress> GetUnconfirmedPeers(
        IDisseminationNamespace disseminationNamespace)
    {
        var membershipSnapshots = _membership.CurrentSnapshots;
        PrunePeerNamespaceConfirmations(membershipSnapshots.AllMembers);
        var participants = membershipSnapshots.GetSnapshot(disseminationNamespace.MembershipScope).Members;
        lock (_peerSupportLock)
        {
            List<SiloAddress>? result = null;
            foreach (var peer in participants)
            {
                if (Equals(peer, _localSilo))
                {
                    continue;
                }

                if (!_confirmedPeerNamespaces.TryGetValue(peer, out var namespaces)
                    || !namespaces.ContainsKey(disseminationNamespace.Name))
                {
                    (result ??= []).Add(peer);
                }
            }

            return result ?? [];
        }
    }

    private void ObserveBroadcastResponse(
        SiloAddress peer,
        DisseminationBroadcastResponse response)
    {
        ConfirmPeerNamespaces(peer, response.Acknowledgments.Keys);
        RevokePeerNamespaces(peer, response.UnsupportedNamespaces);
    }

    private void ConfirmPeerNamespaces(
        SiloAddress peer,
        IEnumerable<DisseminationNamespace> namespaceNames)
    {
        if (Equals(peer, _localSilo)
            || !_membership.CurrentSnapshots.AllMembers.ContainsMember(peer))
        {
            return;
        }

        var now = _timeProvider.GetTimestamp();
        lock (_peerSupportLock)
        {
            Dictionary<DisseminationNamespace, long>? confirmedNamespaces = null;
            foreach (var namespaceName in namespaceNames)
            {
                if (!_namespaces.TryGetValue(namespaceName, out var disseminationNamespace)
                    || !disseminationNamespace.Options.Enabled)
                {
                    continue;
                }

                if (confirmedNamespaces is null
                    && !_confirmedPeerNamespaces.TryGetValue(peer, out confirmedNamespaces))
                {
                    confirmedNamespaces = [];
                    _confirmedPeerNamespaces.Add(peer, confirmedNamespaces);
                }

                confirmedNamespaces![namespaceName] = now;
            }
        }
    }

    private void RevokePeerNamespaces(
        SiloAddress peer,
        IEnumerable<DisseminationNamespace> namespaceNames)
    {
        lock (_peerSupportLock)
        {
            if (!_confirmedPeerNamespaces.TryGetValue(peer, out var confirmedNamespaces))
            {
                return;
            }

            foreach (var namespaceName in namespaceNames)
            {
                confirmedNamespaces.Remove(namespaceName);
            }

            if (confirmedNamespaces.Count == 0)
            {
                _confirmedPeerNamespaces.Remove(peer);
            }
        }
    }

    private void PrunePeerNamespaceConfirmations(DisseminationMembershipSnapshot membership)
    {
        lock (_peerSupportLock)
        {
            foreach (var peer in _confirmedPeerNamespaces.Keys
                .Where(peer => !membership.ContainsMember(peer))
                .ToArray())
            {
                _confirmedPeerNamespaces.Remove(peer);
            }
        }
    }

    private void RecordValueUpdate(DisseminationNamespace namespaceName, DisseminationKey key, long version)
    {
        // Recent successful updates suppress anti-entropy until the namespace's expected cadence elapses.
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
        DisseminationMembershipScope scope,
        SiloAddress member,
        CancellationToken cancellationToken)
    {
        // Prune peer ledgers against the same membership view used to choose tree targets.
        var memberships = await _membership.GetSnapshotsContainingMember(member, scope, cancellationToken);
        await _broadcastQueue.Prune(memberships ?? _membership.CurrentSnapshots, cancellationToken);
        return memberships?.GetSnapshot(scope);
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

        // Publishing is safe only if the namespace can materialize a complete repair for an unknown peer.
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
        // Keep namespace-specific serialization behind one common range and budget contract.
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
        item.TimeToLive <= TimeSpan.Zero;

    private DisseminationBroadcastValue CreateBroadcastValue(
        IDisseminationNamespace disseminationNamespace,
        DisseminationValue value) =>
        new()
        {
            Value = value,
            TimeToLive = disseminationNamespace.Options.StaleItemTtl,
        };

    private void EmitApplyResult(DisseminationNamespace namespaceName, DisseminationBroadcastValue item, SiloAddress sender, DisseminationApplyResult result)
    {
        try
        {
            DisseminationEvents.EmitValue(namespaceName, item.Value, _localSilo, sender, result, item.Value.Payload.Length);
        }
        catch (Exception exception)
        {
            LogDebugDisseminationDiagnosticFailed(_logger, exception, namespaceName, item.Value.Key);
        }

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

    // Prefer the highest terminal version, then a universal full value, then stable peer order.
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

    private readonly record struct AntiEntropyResponseCursor(int Position, long LastAccess);

    private readonly record struct AntiEntropyRepair(
        IDisseminationNamespace Namespace,
        List<DisseminationBroadcastValue> Items,
        SiloAddress Sender);

    private readonly record struct AntiEntropyResponseCandidate(
        IDisseminationNamespace Namespace,
        DigestEntry LocalDigest,
        DigestEntry PeerDigest);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination send to {Peer} failed.")]
    private static partial void LogDebugDisseminationSendFailed(ILogger logger, Exception exception, SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to apply dissemination value from {Sender} for namespace {Namespace}, key {Key}, version {Version}.")]
    private static partial void LogDebugDisseminationValueApplyFailed(ILogger logger, Exception exception, SiloAddress sender, DisseminationNamespace @namespace, DisseminationKey key, long version);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination apply diagnostic failed for namespace {Namespace}, key {Key}.")]
    private static partial void LogDebugDisseminationDiagnosticFailed(
        ILogger logger,
        Exception exception,
        DisseminationNamespace @namespace,
        DisseminationKey key);

}

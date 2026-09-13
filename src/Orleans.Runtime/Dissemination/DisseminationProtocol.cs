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
    private readonly DisseminationSendGate _antiEntropySendGate;
    private readonly CancellationTokenSource _antiEntropyShutdown = new();
    private readonly object _antiEntropyResponseCursorLock = new();
    private readonly Dictionary<SiloAddress, AntiEntropyResponseCursor> _antiEntropyResponseCursors = [];
    private long _antiEntropyResponseCursorAccess;
    private readonly object _receivedBatchCursorLock = new();
    private readonly Dictionary<(SiloAddress Peer, bool AntiEntropy), ReceivedBatchCursor> _receivedBatchCursors = [];
    private long _receivedBatchCursorAccess;
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
        _antiEntropySendGate = new(Math.Max(1, options.CurrentValue.Overlay.AntiEntropyPeerCount));

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
        var receivedTimestamp = _timeProvider.GetTimestamp();
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
        foreach (var namespaceName in batch.Values.Keys)
        {
            if (!TryGetEnabledNamespace(namespaceName, out _))
            {
                unsupportedNamespaces.Add(namespaceName);
            }
        }

        foreach (var (namespaceName, values) in SelectReceivedValues(batch.Sender, batch.Values, options, antiEntropy: false))
        {
            var disseminationNamespace = _namespaces[namespaceName];
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
                DisseminationApplyResult result;
                try
                {
                    result = await ApplyReceivedValue(
                        disseminationNamespace,
                        item,
                        batch.Sender,
                        options,
                        receivedTimestamp,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw;
                }

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

        // A round never queues behind prior rounds: busy destinations and slots wait for a future rotation.
        var leases = AcquireAntiEntropyPeers(membershipSnapshots, options.Overlay.AntiEntropyPeerCount);
        try
        {
            if (leases.Count == 0)
            {
                return;
            }

            using var roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _antiEntropyShutdown.Token);
            var roundCancellationToken = roundCancellation.Token;
            roundCancellationToken.ThrowIfCancellationRequested();
            // Push traffic suppresses redundant checks; admitted peers share this round's digest snapshot.
            var requestDigests = CreateAntiEntropyRequestDigests(_timeProvider.GetTimestamp());
            var requests = CreateAntiEntropyRequests(membershipSnapshots, requestDigests, leases.Keys, options);
            if (requests.Count == 0)
            {
                return;
            }

            var roundLifetime = GetAntiEntropyRoundLifetime(requests.Values);
            using var lifetimeCancellation = new CancellationTokenSource(roundLifetime, _timeProvider);
            using var exchangeCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                roundCancellationToken,
                lifetimeCancellation.Token);
            var responseTasks = new List<Task<DisseminationAntiEntropyResponse?>>(requests.Count);
            DisseminationAntiEntropyResponse?[] responses;
            try
            {
                foreach (var (peer, request) in requests)
                {
                    var responseTask = ExchangeAntiEntropyRequest(
                        peer,
                        request,
                        leases[peer],
                        GetDigestCount(request.Digests),
                        exchangeCancellation.Token,
                        roundCancellationToken,
                        lifetimeCancellation.Token);
                    leases.Remove(peer);
                    responseTasks.Add(responseTask);
                }

                // Each exchange bounds its local wait, so all leases are released before the round completes.
                responses = await Task.WhenAll(responseTasks);
            }
            finally
            {
                // The local wait can win cancellation before the linked transport source is signaled.
                await exchangeCancellation.CancelAsync();
            }

            roundCancellationToken.ThrowIfCancellationRequested();
            await ApplyAntiEntropyResponses(responses, options, roundCancellationToken);
        }
        finally
        {
            foreach (var lease in leases.Values)
            {
                lease.Dispose();
            }
        }
    }

    private Dictionary<SiloAddress, DisseminationSendGate.Lease> AcquireAntiEntropyPeers(
        DisseminationMembershipSnapshots membershipSnapshots,
        int peerCount)
    {
        // AllMembers is a superset of ActiveMembers, preserving a single budget across namespace scopes.
        var selectionScope = _namespaces.Values.Any(static disseminationNamespace =>
            disseminationNamespace.Options.Enabled
            && disseminationNamespace.MembershipScope == DisseminationMembershipScope.AllMembers)
            ? DisseminationMembershipScope.AllMembers
            : DisseminationMembershipScope.ActiveMembers;
        var result = new Dictionary<SiloAddress, DisseminationSendGate.Lease>();
        foreach (var peer in membershipSnapshots.GetSnapshot(selectionScope).SelectAntiEntropyPeers(peerCount))
        {
            if (_antiEntropySendGate.TryAcquire(peer, out var lease))
            {
                result.Add(peer, lease);
            }
        }

        return result;
    }

    private Dictionary<SiloAddress, DisseminationAntiEntropyRequest> CreateAntiEntropyRequests(
        DisseminationMembershipSnapshots membershipSnapshots,
        Dictionary<DisseminationNamespace, List<DigestEntry>> requestDigests,
        IEnumerable<SiloAddress> peers,
        DisseminationOptions options)
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

        foreach (var peer in peers)
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
                    MaxResponseItems = options.MaxBatchItems,
                    MaxResponseBytes = options.MaxBatchBytes,
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
        DisseminationSendGate.Lease lease,
        int requestDigestCount,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken,
        CancellationToken lifetimeCancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exchangeTask = _grainFactory.GetSystemTarget<IDisseminationSystemTarget>(Constants.DisseminationSystemTargetType, peer)
                .ExchangeAntiEntropy(request, cancellationToken);
            exchangeTask.Ignore();
            var response = await exchangeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
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
        finally
        {
            lease.Dispose();
            EmitAntiEntropyAdmissionReleased(peer);
        }
    }

    private void EmitAntiEntropyAdmissionReleased(SiloAddress peer)
    {
        try
        {
            DisseminationEvents.EmitSendGate(_localSilo, peer, kind: "repair", stage: "released");
        }
        catch (Exception exception)
        {
            LogDebugDisseminationAdmissionDiagnosticFailed(_logger, exception, peer);
        }
    }

    private async Task ApplyAntiEntropyResponses(
        DisseminationAntiEntropyResponse?[] responses,
        DisseminationOptions options,
        CancellationToken cancellationToken)
    {
        // Completed exchanges get a separate local application window, even if another peer used its whole
        // transport budget. No remote clock or transmission delay is inferred from a relative wire lifetime.
        var receivedTimestamp = _timeProvider.GetTimestamp();
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
            foreach (var (namespaceName, values) in SelectReceivedValues(response.Sender, response.Values, options, antiEntropy: true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var disseminationNamespace = _namespaces[namespaceName];

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
                        receivedTimestamp,
                        cancellationToken);
                }
            }
        }
    }

    private Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> SelectReceivedValues(
        SiloAddress peer,
        Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>> values,
        DisseminationOptions options,
        bool antiEntropy)
    {
        var result = new Dictionary<DisseminationNamespace, List<DisseminationBroadcastValue>>();
        var totalCount = values.Values.Sum(static entries => (long)entries.Count);
        var cursorKey = (peer, antiEntropy);
        long position;
        lock (_receivedBatchCursorLock)
        {
            position = _receivedBatchCursors.TryGetValue(cursorKey, out var cursor)
                && cursor.TotalCount == totalCount && cursor.Position < totalCount
                ? cursor.Position
                : 0;
        }

        var skip = position;
        var examined = 0;
        var byteCount = 0;
        foreach (var (namespaceName, entries) in values)
        {
            if (skip >= entries.Count)
            {
                skip -= entries.Count;
                continue;
            }

            if (!TryGetEnabledNamespace(namespaceName, out var disseminationNamespace))
            {
                position += entries.Count - skip;
                skip = 0;
                continue;
            }

            for (var index = (int)skip; index < entries.Count; index++)
            {
                if (examined >= options.MaxBatchItems || byteCount >= options.MaxBatchBytes)
                {
                    goto Complete;
                }

                var item = entries[index];
                var payloadBytes = item.Value.Payload.Length;
                if (!ValidatePayloadSize(disseminationNamespace, item.Value, options))
                {
                    // Oversized entries consume inspection capacity, but must not pin the receive cursor.
                    examined++;
                    position++;
                    continue;
                }

                if (payloadBytes > options.MaxBatchBytes - byteCount)
                {
                    // Preserve this candidate for a fresh budget instead of discarding a repair-chain suffix.
                    goto Complete;
                }

                if (!result.TryGetValue(namespaceName, out var selected))
                {
                    selected = [];
                    result.Add(namespaceName, selected);
                }

                selected.Add(item);
                examined++;
                byteCount += payloadBytes;
                position++;
            }

            skip = 0;
        }

    Complete:
        var members = _membership.CurrentSnapshots.AllMembers;
        lock (_receivedBatchCursorLock)
        {
            // Do not wrap within a batch: doing so would reorder a key's delta chain. Subsequent deliveries
            // resume an ordered, bounded interval, allowing later keys past a repeatedly rejected/hot prefix.
            if (position < totalCount)
            {
                _receivedBatchCursors[cursorKey] = new(position, totalCount, ++_receivedBatchCursorAccess);
            }
            else
            {
                _receivedBatchCursors.Remove(cursorKey);
            }

            var nonMembers = _receivedBatchCursors
                .Where(entry => !members.ContainsMember(entry.Key.Peer))
                .OrderByDescending(static entry => entry.Value.LastAccess)
                .Skip(MaxRetainedNonMemberResponseCursors)
                .Select(static entry => entry.Key)
                .ToArray();
            foreach (var key in nonMembers)
            {
                _receivedBatchCursors.Remove(key);
            }
        }

        return result;
    }

    public ValueTask<DisseminationAntiEntropyResponse> ReceiveAntiEntropy(
        DisseminationAntiEntropyRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.MaxResponseItems is { } maxResponseItems)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseItems, nameof(request.MaxResponseItems));
        }

        if (request.MaxResponseBytes is { } maxResponseBytes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResponseBytes, nameof(request.MaxResponseBytes));
        }

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

        // Honor the recipient's budget at the responder's existing fair cursor. Independently truncating
        // rotating responses at the receiver can repeatedly omit the same keys when the limits differ.
        var maxResponseItems = Math.Min(options.MaxBatchItems, request.MaxResponseItems ?? int.MaxValue);
        var maxResponseBytes = Math.Min(options.MaxBatchBytes, request.MaxResponseBytes ?? int.MaxValue);
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
                maxResponseItems - valueCount,
                maxResponseBytes - byteCount,
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
                        maxResponseItems,
                        maxResponseBytes,
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
        long receivedTimestamp,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ApplyReceivedValueCore(
                disseminationNamespace,
                item,
                sender,
                options,
                receivedTimestamp,
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
        long receivedTimestamp,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var namespaceName = disseminationNamespace.Name;
        if (!ValidatePayloadSize(disseminationNamespace, item.Value, options))
        {
            return DisseminationApplyResult.Rejected;
        }

        var lifetime = item.TimeToLive < disseminationNamespace.Options.StaleItemTtl
            ? item.TimeToLive
            : disseminationNamespace.Options.StaleItemTtl;
        var remainingLifetime = lifetime - _timeProvider.GetElapsedTime(receivedTimestamp);
        if (remainingLifetime <= TimeSpan.Zero)
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

        using var lifetimeCancellation = new CancellationTokenSource(
            remainingLifetime < MaxAntiEntropyRoundLifetime ? remainingLifetime : MaxAntiEntropyRoundLifetime,
            _timeProvider);
        using var applicationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetimeCancellation.Token);
        DisseminationApplyResult result;
        try
        {
            var application = disseminationNamespace.ApplyValueAsync(item.Value, applicationCancellation.Token);
            if (application.IsCompletedSuccessfully)
            {
                result = application.Result;
            }
            else
            {
                // Only this local wait is bounded. Namespace owners must observe cancellation before mutating
                // queued state; arbitrary implementations which ignore the token cannot be forcibly stopped.
                var applicationTask = application.AsTask();
                applicationTask.Ignore();
                result = await applicationTask.WaitAsync(applicationCancellation.Token);
            }

            applicationCancellation.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (
            lifetimeCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            result = DisseminationApplyResult.Obsolete;
        }

        EmitApplyResult(namespaceName, item, sender, result);
        if (result is DisseminationApplyResult.Applied)
        {
            RecordValueUpdate(namespaceName, item.Value.Key, item.Value.ToVersion);
        }

        return result;
    }

    internal Task FlushPendingBroadcast(CancellationToken cancellationToken) =>
        _broadcastQueue.FlushPendingBroadcast(cancellationToken);

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        _antiEntropySendGate.Stop();
        await _antiEntropyShutdown.CancelAsync();
        await _broadcastQueue.StopAsync(cancellationToken);
    }

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
        try
        {
            DisseminationEvents.EmitPayloadDrop(namespaceName, value, _localSilo, "oversize", value.Payload.Length);
        }
        catch (Exception exception)
        {
            LogDebugDisseminationDiagnosticFailed(_logger, exception, namespaceName, value.Key);
        }

        try
        {
            DisseminationInstruments.OnPayloadDropped(namespaceName, "oversize");
        }
        catch (Exception exception)
        {
            LogDebugDisseminationDiagnosticFailed(_logger, exception, namespaceName, value.Key);
        }

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

        try
        {
            DisseminationInstruments.OnValueApplied(namespaceName, result);
        }
        catch (Exception exception)
        {
            LogDebugDisseminationDiagnosticFailed(_logger, exception, namespaceName, item.Value.Key);
        }
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

    private readonly record struct ReceivedBatchCursor(long Position, long TotalCount, long LastAccess);

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
        Message = "Dissemination admission diagnostic for {Peer} failed.")]
    private static partial void LogDebugDisseminationAdmissionDiagnosticFailed(ILogger logger, Exception exception, SiloAddress peer);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Failed to apply dissemination value from {Sender} for namespace {Namespace}, key {Key}, version {Version}.")]
    private static partial void LogDebugDisseminationValueApplyFailed(ILogger logger, Exception exception, SiloAddress sender, DisseminationNamespace @namespace, DisseminationKey key, long version);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Dissemination value diagnostic failed for namespace {Namespace}, key {Key}.")]
    private static partial void LogDebugDisseminationDiagnosticFailed(
        ILogger logger,
        Exception exception,
        DisseminationNamespace @namespace,
        DisseminationKey key);

}

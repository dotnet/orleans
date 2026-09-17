using Orleans.Internal;

namespace Orleans.Runtime.Dissemination;

internal sealed partial class DisseminationProtocol
{
    private readonly DisseminationSendGate _publicationSendGate = new(1);
    private readonly CancellationTokenSource _publicationShutdown = new();

    public async ValueTask<DisseminationPublicationReceipt> PublishAggregated(
        IDisseminationNamespace disseminationNamespace,
        DisseminationKey key,
        long version,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var admission = _admission.TryEnter();
        if (!admission.Entered)
        {
            return Reject("stopping");
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _publicationShutdown.Token);
        try
        {
            // Held load receipts have independent admission, keeping membership's peer pumps available.
            using var lease = await _publicationSendGate.AcquireAsync(_localSilo, cancellation.Token);
            var options = _options.CurrentValue;
            if (!options.Enabled || !disseminationNamespace.Options.Enabled)
            {
                return Reject("disabled");
            }

            if (disseminationNamespace.RoutingMode != DisseminationRoutingMode.AggregationTree
                || !Equals(key.Value, _localSilo)
                || version <= 0
                || disseminationNamespace.GetVersion(key) < version)
            {
                return Reject("invalid-contribution");
            }

            var membership = await GetMembershipSnapshotForRouting(
                disseminationNamespace.MembershipScope, _localSilo, cancellation.Token);
            if (membership is null)
            {
                return Reject("membership-unavailable");
            }

            var repairRequest = new DisseminationRepairRequest(
                key, fromVersion: null,
                options.MaxBatchBytes, disseminationNamespace.Options.MaxPayloadBytes);
            var repair = disseminationNamespace.CreateRepair(repairRequest);
            if (repair.Status != DisseminationRepairStatus.Produced
                || repair.Version < version
                || !ValidateRepair(disseminationNamespace, repairRequest, repair, options))
            {
                return Reject("invalid-repair");
            }

            var value = repair.Value;
            RecordValueUpdate(disseminationNamespace.Name, key, value.ToVersion);
            DisseminationPublicationReceipt receipt;
            if (membership.IsAggregationRoot)
            {
                var root = GetRootBatcher(disseminationNamespace);
                if (root is null)
                {
                    return Reject("stopping");
                }

                receipt = await root.PublishAsync(new(key, value.ToVersion, true), cancellation.Token);
            }
            else
            {
                var rootAddress = membership.Members[0];
                var target = _grainFactory.GetSystemTarget<IDisseminationSystemTarget>(
                    Constants.DisseminationSystemTargetType, rootAddress);
                var request = new DisseminationPublicationRequest
                {
                    Sender = _localSilo,
                    Namespace = disseminationNamespace.Name,
                    Value = new() { Value = value, TimeToLive = disseminationNamespace.Options.StaleItemTtl },
                };
                var response = target.PublishAggregated(request, cancellation.Token);
                response.Ignore();
                receipt = await response.WaitAsync(cancellation.Token);
                if (receipt.Accepted)
                {
                    ConfirmPeerNamespaces(rootAddress, [disseminationNamespace.Name]);
                }
            }

            EmitPublication(disseminationNamespace.Name, receipt.Accepted,
                receipt.Accepted ? "none" : "cohort-rejected");
            return receipt;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        DisseminationPublicationReceipt Reject(string reason)
        {
            EmitPublication(disseminationNamespace.Name, accepted: false, reason);
            return default;
        }
    }

    public async Task<DisseminationPublicationReceipt> ReceivePublication(
        DisseminationPublicationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var admission = _admission.TryEnter();
        ObjectDisposedException.ThrowIf(!admission.Entered, this);
        var receivedTimestamp = _timeProvider.GetTimestamp();
        var options = _options.CurrentValue;
        if (!options.Enabled || !TryGetEnabledNamespace(request.Namespace, out var ns))
        {
            return Reject("disabled");
        }

        if (ns.RoutingMode != DisseminationRoutingMode.AggregationTree
            || !Equals(request.Value.Value.Key.Value, request.Sender)
            || request.Value.Value.FromVersion != 0
            || options.MaxBatchItems < 1
            || !ValidatePayloadSize(ns, request.Value.Value, options.MaxBatchBytes))
        {
            return Reject("invalid-contribution");
        }

        var membership = await GetMembershipSnapshotForRouting(ns.MembershipScope, request.Sender, cancellationToken);
        if (membership is null || !membership.IsAggregationRoot)
        {
            return Reject("root-changed");
        }

        var result = await ApplyReceivedValue(ns, request.Value, request.Sender, options, receivedTimestamp, cancellationToken);
        if (result is not (DisseminationApplyResult.Applied or DisseminationApplyResult.Duplicate))
        {
            return Reject("application-rejected");
        }

        ConfirmPeerNamespaces(request.Sender, [ns.Name]);
        var root = GetRootBatcher(ns);
        if (root is null)
        {
            return Reject("stopping");
        }

        var receipt = await root.PublishAsync(
            new(request.Value.Value.Key, request.Value.Value.ToVersion, result == DisseminationApplyResult.Applied),
            cancellationToken);
        EmitPublication(ns.Name, receipt.Accepted, receipt.Accepted ? "cohort-sealed" : "cohort-rejected");
        return receipt;

        DisseminationPublicationReceipt Reject(string reason)
        {
            EmitPublication(request.Namespace, accepted: false, reason);
            return default;
        }
    }
}

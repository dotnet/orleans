using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.GrainReferences;
using Orleans.Serialization.Invocation;
using Microsoft.Extensions.Options;
using Orleans.Metadata;
using Orleans.Runtime.Placement;

namespace Orleans.Runtime;

internal sealed class InterClusterRequestReceiver(
    IInternalGrainFactory grainFactory,
    IRuntimeClient runtimeClient,
    UniversalReferenceBindingResolver bindingResolver,
    IOptions<MetaclusterOptions> metaclusterOptions,
    ClusterLocatorResolver clusterLocatorResolver,
    IMetaclusterTopologyProvider topologyProvider,
    GrainInterfaceTypeResolver interfaceTypeResolver,
    IInterClusterRequestAuthorizer authorizer,
    IClusterMembershipService clusterMembershipService) : IInterClusterRequestReceiver
{
    public async ValueTask<Response> ReceiveRequest(
        ClusterIdentity source,
        UniversalReference target,
        IInvokable request,
        InvokeMethodOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(source.ServiceId, bindingResolver.ServiceId, StringComparison.Ordinal)
            || !string.Equals(target.ServiceId, bindingResolver.ServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Source service '{source.ServiceId}' and target service '{target.ServiceId}' must match local service '{bindingResolver.ServiceId}'.");
        }

        await authorizer.Authorize(source, target, cancellationToken);

        var topology = await topologyProvider.GetTopology(cancellationToken);
        if (!topology.Clusters.TryGetValue(source.ClusterId, out var sourceCluster)
            || sourceCluster.State == MetaclusterClusterState.Removed)
        {
            throw new UnauthorizedAccessException(
                $"Source cluster '{source}' is not an active member of topology epoch '{topology.Epoch}'.");
        }

        if (!topology.Clusters.TryGetValue(bindingResolver.ClusterId, out var localCluster)
            || localCluster.State == MetaclusterClusterState.Removed)
        {
            throw new InvalidOperationException(
                $"Local cluster '{bindingResolver.ClusterId}' is not an active member of topology epoch '{topology.Epoch}'.");
        }

        if (target.Binding != UniversalReferenceBinding.Cluster)
        {
            throw new InvalidOperationException(
                $"Federated target '{target}' must be cluster-bound before it reaches cluster ingress.");
        }

        if (!string.Equals(target.ClusterId, bindingResolver.ClusterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cluster-bound target '{target.ClusterId}' cannot be dispatched by cluster '{bindingResolver.ClusterId}'.");
        }

        var requestInterfaceType = interfaceTypeResolver.GetGrainInterfaceType(request.GetInterfaceType());
        if (target.InterfaceType.IsDefault)
        {
            throw new InvalidOperationException(
                $"Federated target '{target}' must identify the interface being invoked.");
        }

        if (target.InterfaceType != requestInterfaceType)
        {
            throw new InvalidOperationException(
                $"Request interface '{requestInterfaceType}' does not match target interface '{target.InterfaceType}'.");
        }

        if (request is not IRequest trustedRequest)
        {
            throw new InvalidOperationException(
                $"Federated request '{request.GetType()}' does not expose trusted invocation metadata.");
        }

        var trustedOptions = trustedRequest.Options;
        if (options != trustedOptions)
        {
            throw new UnauthorizedAccessException(
                $"Federated invocation options '{options}' do not match trusted request options '{trustedOptions}'.");
        }

        if (SystemTargetGrainId.TryParse(target.GrainId, out var systemTargetId))
        {
            if (!metaclusterOptions.Value.ExportedSystemTargets.Contains(target.GrainId.Type.ToString()))
            {
                throw new UnauthorizedAccessException(
                    $"System target type '{target.GrainId.Type}' is not exported to the metacluster.");
            }

            var targetSilo = systemTargetId.GetSiloAddress();
            if (!clusterMembershipService.CurrentSnapshot.Members.TryGetValue(targetSilo, out var member)
                || member.Status != SiloStatus.Active)
            {
                throw new UnauthorizedAccessException(
                    $"System target '{target.GrainId}' does not identify an active member of the local cluster.");
            }
        }

        if (clusterLocatorResolver.Resolve(target.GrainId.Type) is IClusterOwnershipValidator ownershipValidator)
        {
            await ownershipValidator.ValidateLocalOwnership(
                target.GrainId,
                bindingResolver.ClusterId,
                cancellationToken);
        }

        var localTarget = UniversalReference.CreateCluster(
            target.GrainId,
            target.InterfaceType,
            target.ServiceId,
            bindingResolver.ClusterId);
        var reference = (GrainReference)grainFactory.GetGrain(localTarget);
        ApplyCancellationToken(request, cancellationToken);
        if ((trustedOptions & InvokeMethodOptions.OneWay) != 0)
        {
            runtimeClient.SendRequest(reference, request, context: null, trustedOptions);
            return Response.Completed;
        }

        var completion = ResponseCompletionSourcePool.Get();
        runtimeClient.SendRequest(reference, request, completion, trustedOptions);
        return await completion.AsValueTask().AsTask().WaitAsync(cancellationToken);
    }

    private static void ApplyCancellationToken(IInvokable request, CancellationToken cancellationToken)
    {
        if (!request.IsCancellable || request.GetMethod() is not { } method)
        {
            return;
        }

        var parameters = method.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
        {
            if (parameters[index].ParameterType == typeof(CancellationToken))
            {
                request.SetArgument(index, cancellationToken);
                return;
            }
        }
    }
}

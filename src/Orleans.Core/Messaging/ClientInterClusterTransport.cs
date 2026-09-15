using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.CodeGeneration;
using Orleans.Configuration;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// Sends inter-cluster requests through a connected Orleans client for each destination cluster.
/// </summary>
public sealed class ClientInterClusterTransport(
    IInterClusterClientProvider clientProvider,
    IOptions<ClusterOptions> clusterOptions) : IInterClusterTransport
{
    private readonly ClusterIdentity _localCluster = new(
        clusterOptions.Value.ServiceId,
        clusterOptions.Value.ClusterId);

    /// <inheritdoc/>
    public async ValueTask<Response> SendRequest(
        ClusterIdentity destination,
        UniversalReference target,
        IInvokable request,
        InvokeMethodOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (destination.ServiceId != _localCluster.ServiceId
            || target.ServiceId != _localCluster.ServiceId)
        {
            throw new InvalidOperationException(
                $"Destination '{destination}' and target service '{target.ServiceId}' must match local service '{_localCluster.ServiceId}'.");
        }

        if (target.Binding == UniversalReferenceBinding.Cluster
            && target.ClusterId != destination.ClusterId)
        {
            throw new InvalidOperationException(
                $"Cluster-bound target '{target.ClusterId}' does not match destination cluster '{destination.ClusterId}'.");
        }

        var client = await clientProvider.GetClient(destination, cancellationToken);
        var relay = client.GetGrain<IInterClusterRelay>(_localCluster.ClusterId);
        var routedTarget = target.Binding == UniversalReferenceBinding.Virtual
            ? UniversalReference.CreateCluster(
                target.GrainId,
                target.InterfaceType,
                target.ServiceId,
                destination.ClusterId)
            : target;
        return await relay.Forward(_localCluster, routedTarget, request, options, cancellationToken);
    }
}

using System.Threading.Tasks;
using System.Threading;
using Orleans.CodeGeneration;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

[GrainType("interclusterrelay"), Reentrant]
internal sealed class InterClusterRelayGrain(IInterClusterRequestReceiver receiver) : Grain, IInterClusterRelay
{
    public ValueTask<Response> Forward(
        ClusterIdentity source,
        UniversalReference target,
        IInvokable request,
        InvokeMethodOptions options,
        CancellationToken cancellationToken)
        => receiver.ReceiveRequest(source, target, request, options, cancellationToken);
}

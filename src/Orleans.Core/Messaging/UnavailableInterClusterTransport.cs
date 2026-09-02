using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

internal sealed class UnavailableInterClusterTransport : IInterClusterTransport
{
    public ValueTask<Response> SendRequest(
        ClusterIdentity destination,
        UniversalReference target,
        IInvokable request,
        InvokeMethodOptions options,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<Response>(
            new NotSupportedException($"Federation transport to cluster '{destination}' is not configured."));
}

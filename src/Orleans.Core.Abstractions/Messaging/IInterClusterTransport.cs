using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// Sends Orleans requests to another cluster in the same service.
/// </summary>
public interface IInterClusterTransport
{
    /// <summary>
    /// Sends a request to another cluster.
    /// </summary>
    ValueTask<Response> SendRequest(
        ClusterIdentity destination,
        UniversalReference target,
        IInvokable request,
        InvokeMethodOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dispatches a request received from another cluster.
/// </summary>
public interface IInterClusterRequestReceiver
{
    /// <summary>
    /// Dispatches a request in the local cluster.
    /// </summary>
    ValueTask<Response> ReceiveRequest(
        ClusterIdentity source,
        UniversalReference target,
        IInvokable request,
        InvokeMethodOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorizes requests received from another cluster.
/// </summary>
public interface IInterClusterRequestAuthorizer
{
    /// <summary>
    /// Authorizes an inter-cluster request before local dispatch.
    /// </summary>
    ValueTask Authorize(
        ClusterIdentity source,
        UniversalReference target,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Receives forwarded Orleans requests in a destination cluster.
/// </summary>
public interface IInterClusterRelay : IGrainWithStringKey
{
    /// <summary>
    /// Forwards a request into the relay's local cluster.
    /// </summary>
    ValueTask<Response> Forward(
        ClusterIdentity source,
        UniversalReference target,
        IInvokable request,
        InvokeMethodOptions options,
        CancellationToken cancellationToken);
}

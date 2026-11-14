using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// A request which a grain context receives through <see cref="IGrainContext.ReceiveLocalRequest"/> and which receives its own response.
/// </summary>
/// <remarks>
/// The grain context invokes the request on the activation, through its incoming grain call filters, passes the
/// response to <see cref="Complete"/>, and then disposes the request. Call filters, logs, and traces observe the request's interface name, method name, and arguments.
/// The request and its response are passed by reference rather than copied, so a request which shares mutable state with its caller copies it itself.
/// </remarks>
public interface ILocalRequest : IInvokable
{
    /// <summary>
    /// Completes the request with its outcome.
    /// </summary>
    /// <param name="response">
    /// The response produced by the request and its call filters, or an exception if the grain context rejects the request.
    /// The grain context rejects a request with an <see cref="OrleansMessageRejectionException"/> if it deactivates before the request starts.
    /// </param>
    /// <remarks>
    /// The request owns its outcome, including reporting or logging failures, and accepts the first outcome it receives.
    /// This method can be called from any thread, so implementations should complete quickly and without blocking.
    /// </remarks>
    void Complete(Response response);
}

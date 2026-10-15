using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

/// <summary>
/// A request which a grain context receives through <see cref="IGrainContext.ReceiveLocalRequest"/> and which receives its own response.
/// </summary>
/// <remarks>
/// The grain context invokes the request on the activation, through its incoming grain call filters, and then passes the
/// response to <see cref="Complete"/>. Call filters, logs, and traces observe the request's interface name, method name, and arguments.
/// </remarks>
public interface ILocalRequest : IInvokable
{
    /// <summary>
    /// Completes the request with its outcome.
    /// </summary>
    /// <param name="response">
    /// The response produced by the request and its call filters, or an exception if the grain context rejects the request.
    /// The grain context rejects a request with an <see cref="OrleansMessageRejectionException"/> if it deactivates before the request starts,
    /// and with a <see cref="LimitExceededException"/> if <see cref="LocalRequestOptions.EnforceQueueLimit"/> is set and its queue is full.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the request reports the response's exception to an observer; otherwise, <see langword="false"/>,
    /// in which case the runtime logs a failure as a warning.
    /// </returns>
    /// <remarks>
    /// The grain context calls this method once for each time it receives the request.
    /// It can be called from any thread, so implementations should complete quickly and without blocking.
    /// </remarks>
    bool Complete(Response response);
}

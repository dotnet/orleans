using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

internal static partial class LocalRequests
{
    /// <summary>
    /// Completes the local request which <paramref name="message"/> carries with <paramref name="response"/> and then disposes it.
    /// </summary>
    /// <remarks>
    /// A failure in the request's completion or disposal is logged rather than thrown, so callers completing a batch of requests complete every request.
    /// </remarks>
    public static void Complete(Message message, Response response, ILogger logger)
    {
        Debug.Assert(message.IsLocalOnly);
        var request = (ILocalRequest)message.BodyObject!;
        try
        {
            try
            {
                request.Complete(response);
            }
            finally
            {
                request.Dispose();
            }
        }
        catch (Exception exception)
        {
            LogErrorCompletingLocalRequest(logger, exception, request);
        }
    }

    /// <summary>
    /// Completes the local request which <paramref name="message"/> carries with <paramref name="exception"/> and then disposes it.
    /// </summary>
    /// <inheritdoc cref="Complete(Message, Response, ILogger)" path="/remarks"/>
    public static void Reject(Message message, Exception exception, ILogger logger) => Complete(message, Response.FromException(exception), logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error completing local request {Request}."
    )]
    private static partial void LogErrorCompletingLocalRequest(ILogger logger, Exception exception, ILocalRequest request);
}

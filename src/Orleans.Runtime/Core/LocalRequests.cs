using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

internal static partial class LocalRequests
{
    /// <summary>
    /// Completes <paramref name="request"/> with <paramref name="response"/> and then disposes it.
    /// </summary>
    /// <remarks>
    /// A failure in the request's completion or disposal is logged rather than thrown, so callers completing a batch of requests complete every request.
    /// </remarks>
    public static void Complete(ILocalRequest request, Response response, ILogger logger)
    {
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

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error completing local request {Request}."
    )]
    private static partial void LogErrorCompletingLocalRequest(ILogger logger, Exception exception, ILocalRequest request);
}

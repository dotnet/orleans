using System;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime;

// With a runtime logger, cleanup must not replace a call/transport failure or interrupt draining.
// A missing logger must not hide a cleanup failure.
internal static partial class InvokableArgumentResources
{
    internal static void Complete(IInvokableArgumentOwner? owner, ILogger? logger)
    {
        if (owner is null)
        {
            return;
        }

        try
        {
            owner.CompleteArgumentResources();
        }
        catch (Exception exception)
        {
            if (logger is null)
            {
                throw;
            }

            LogCleanupFailure(logger, exception);
        }
    }

    internal static void Release(IInvokableArgumentOwner? owner, ILogger? logger)
    {
        if (owner is null)
        {
            return;
        }

        try
        {
            owner.ReleaseArgumentResources();
        }
        catch (Exception exception)
        {
            if (logger is null)
            {
                throw;
            }

            LogCleanupFailure(logger, exception);
        }
    }

    internal static void Dispose(IInvokable request, ILogger? logger)
    {
        try
        {
            request.Dispose();
        }
        catch (Exception exception)
        {
            if (logger is null)
            {
                throw;
            }

            LogCleanupFailure(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error releasing explicitly owned RPC argument resources")]
    private static partial void LogCleanupFailure(ILogger logger, Exception exception);
}
